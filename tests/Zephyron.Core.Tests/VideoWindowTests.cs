using Zephyron.Zapret;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>Малое окно для узлов видео под фильтром: профиль SYN, шаг слежения, перехват пустых пакетов.</summary>
public sealed class VideoWindowTests
{
    [Fact]
    public void SynProfileTakesGoogleBeforeProtocolIsKnown()
    {
        var profile = VideoWindow.SynProfile();

        Assert.Equal("--new", profile[0]);
        Assert.Contains("--filter-l7=unknown", profile);
        Assert.Contains(profile, a => a.StartsWith("--ipset-ip=") && a.Contains("74.125.0.0/16"));
        Assert.Equal("--lua-desync=zy_video_window:syn", profile[^1]);
    }

    [Fact]
    public void WatchStepGoesFirstAndKeepsSectionPayload()
    {
        string[] section = ["--filter-tcp=443", "--hostlist=lists/googlevideo.txt", "--payload=tls_client_hello", "--lua-desync=multidisorder:pos=1"];

        Assert.Equal(
            ["--filter-tcp=443", "--hostlist=lists/googlevideo.txt", "--payload=tls_client_hello",
             "--payload=all", "--lua-desync=zy_video_window", "--payload=tls_client_hello", "--lua-desync=multidisorder:pos=1"],
            VideoWindow.Watched(section));
    }

    [Theory]
    [InlineData("--hostlist=lists/googlevideo.txt", "--filter-tcp=80,443", true)]
    [InlineData("--hostlist=lists/youtube.txt", "--filter-tcp=80,443", true)]
    [InlineData("--hostlist=lists/discord.txt", "--filter-tcp=80,443", false)]
    [InlineData("--hostlist=lists/googlevideo.txt", "--filter-udp=443", false)]
    public void VideoSectionsAreFoundByTheirLists(string list, string filter, bool video) =>
        Assert.Equal(video, VideoWindow.IsVideoSection([filter, list, "--lua-desync=pass"]));

    [Fact]
    public void SynDataSectionsByAddressSpareGoogle()
    {
        string[] discord = ["--filter-tcp=443", "--ipset=lists/ipset-discord.txt", "--lua-desync=send:repeats=3", "--lua-desync=syndata:blob=tls_google"];
        string[] byName = ["--filter-tcp=443", "--hostlist=lists/discord.txt", "--lua-desync=syndata:blob=tls_google"];

        Assert.Contains(VideoWindow.SparedFromSynData(discord), a => a.StartsWith("--ipset-exclude-ip=") && a.Contains("74.125.0.0/16"));
        Assert.Equal(byName, VideoWindow.SparedFromSynData(byName));
    }

    [Fact]
    public void EmptyAcksOnlyToGoogle()
    {
        var arguments = new List<string> { "--wf-raw-filter=(x)", "--wf-tcp-out=443" };

        Assert.True(VideoWindow.CaptureEmptyAcks(arguments));
        Assert.Equal("--wf-tcp-empty=1", arguments[0]);
        Assert.Contains(arguments, a => a.StartsWith("--wf-raw-filter=((x)) and (!tcp or tcp.Syn")
            && a.Contains("(ip.DstAddr>=74.125.0.0 and ip.DstAddr<=74.125.255.255)"));

        Assert.False(VideoWindow.CaptureEmptyAcks(["--wf-raw-filter=@file.txt"]));
    }

    [Theory]
    [InlineData("74.125.154.103", true)]
    [InlineData("173.194.18.135", true)]
    [InlineData("195.68.132.108", false)]
    public void GoogleNetworksAreKnown(string address, bool google) =>
        Assert.Equal(google, VideoWindow.IsGoogle(System.Net.IPAddress.Parse(address)));

    [Theory]
    [InlineData("74.125.0.0/16", "(ip.DstAddr>=74.125.0.0 and ip.DstAddr<=74.125.255.255)")]
    [InlineData("64.233.160.0/19", "(ip.DstAddr>=64.233.160.0 and ip.DstAddr<=64.233.191.255)")]
    [InlineData("142.250.0.0/15", "(ip.DstAddr>=142.250.0.0 and ip.DstAddr<=142.251.255.255)")]
    public void RangeCoversCidr(string cidr, string expected) => Assert.Equal(expected, VideoWindow.Range(cidr));
}

/// <summary>Окно в строке winws2 на настоящем пресете.</summary>
public sealed class VideoWindowCommandLineTests
{
    private static ZapretPreset Preset() =>
        new PresetReader().Load(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "presets", "Universal V12 Lite.txt"));

    [Fact]
    public void SynProfileFirstAndVideoSectionsWatched()
    {
        var preset = Preset();
        var arguments = WinwsCommandLine.Build(preset, videoWindow: true).ToList();

        int syn = arguments.IndexOf("--name=Zephyron: узлы видео (SYN)");
        int firstSection = arguments.FindIndex(a => a.StartsWith("--name=", StringComparison.Ordinal) && preset.Sections.Any(s => s.RawArguments.Contains(a)));

        Assert.True(syn >= 0 && syn < firstSection);
        Assert.Contains($"--lua-init=@lua/{VideoWindow.Module}", arguments);
        Assert.Contains("--wf-tcp-empty=1", arguments);
        Assert.True(arguments.Count(a => a == "--lua-desync=zy_video_window") >= 2);
        Assert.Contains(arguments, a => a.StartsWith("--ipset-exclude-ip=") && a.Contains("173.194.0.0/16"));
    }

    [Fact]
    public void OwnVideoRecipeIsWatchedToo()
    {
        var list = Path.Combine(Path.GetTempPath(), "own-video-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllLines(list, ["googlevideo.com", "youtube.com"]);

        try
        {
            var own = new List<OwnDesyncProfile>
            {
                new() { Name = "Google (сервисы)", Steps = ["fake:blob=tls_google:repeats=6", "multidisorder:pos=1"], HostListPath = list },
            };

            var arguments = WinwsCommandLine.Build(Preset(), own: own, videoWindow: true).ToList();

            int syn = arguments.IndexOf("--name=Zephyron: узлы видео (SYN)");
            int mine = arguments.IndexOf("--name=Zephyron: Google (сервисы)");

            Assert.True(syn >= 0 && syn < mine);
            Assert.Equal("--lua-desync=zy_video_window", arguments[arguments.IndexOf("--lua-desync=fake:blob=tls_google:repeats=6") - 2]);
        }
        finally
        {
            File.Delete(list);
        }
    }

    [Fact]
    public void NoWindowWhenOff()
    {
        var arguments = WinwsCommandLine.Build(Preset()).ToList();

        Assert.DoesNotContain(arguments, a => a.StartsWith("--lua-desync=zy_video_window", StringComparison.Ordinal));
        Assert.DoesNotContain("--wf-tcp-empty=1", arguments);
    }
}
