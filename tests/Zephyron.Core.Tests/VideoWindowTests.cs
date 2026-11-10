using Zephyron.Zapret;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>Малое окно для узлов видео под фильтром: список адресов, профиль winws2, перехват пустых пакетов.</summary>
public sealed class VideoWindowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "video-window-" + Guid.NewGuid().ToString("N"));

    public VideoWindowTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string List => Path.Combine(_dir, "video-window.txt");

    [Fact]
    public void ListNeverEmptyForWinws()
    {
        // Пустой список winws2 считает «подходит всем» — заглушка обязана стоять всегда.
        VideoWindow.EnsureList(List);
        Assert.Contains(VideoWindow.Placeholder, File.ReadAllLines(List));
        Assert.Empty(VideoWindow.Read(List));

        VideoWindow.Write(["74.125.154.103", "74.125.154.103", "173.194.18.135"], List);
        Assert.Equal(["173.194.18.135", "74.125.154.103"], VideoWindow.Read(List));
        Assert.Contains(VideoWindow.Placeholder, File.ReadAllLines(List));

        VideoWindow.Write([], List);
        Assert.Equal([VideoWindow.Placeholder], File.ReadAllLines(List));
    }

    [Fact]
    public void ProfileOpensWithWindowThenVideoSteps()
    {
        var profile = VideoWindow.Profile(@"C:\z\runtime\video-window.txt", ["--lua-desync=multidisorder:pos=1"]);

        Assert.Equal("--new", profile[0]);
        Assert.Contains("--ipset=C:/z/runtime/video-window.txt", profile);
        Assert.True(profile.ToList().IndexOf("--lua-desync=zy_video_window") < profile.ToList().IndexOf("--payload=tls_client_hello"));
        Assert.Equal("--lua-desync=multidisorder:pos=1", profile[^1]);
    }

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
    [InlineData("74.125.0.0/16", "(ip.DstAddr>=74.125.0.0 and ip.DstAddr<=74.125.255.255)")]
    [InlineData("64.233.160.0/19", "(ip.DstAddr>=64.233.160.0 and ip.DstAddr<=64.233.191.255)")]
    [InlineData("142.250.0.0/15", "(ip.DstAddr>=142.250.0.0 and ip.DstAddr<=142.251.255.255)")]
    public void RangeCoversCidr(string cidr, string expected) => Assert.Equal(expected, VideoWindow.Range(cidr));
}

/// <summary>Профиль окна в строке winws2 на настоящем пресете.</summary>
public sealed class VideoWindowCommandLineTests
{
    private static ZapretPreset Preset() =>
        new PresetReader().Load(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "presets", "Universal V12 Lite.txt"));

    [Fact]
    public void WindowProfileGoesBeforePresetSectionsAndDiscordSparesGoogle()
    {
        var preset = Preset();
        var arguments = WinwsCommandLine.Build(preset, videoWindowList: @"C:\z\runtime\video-window.txt").ToList();

        int window = arguments.IndexOf("--name=Zephyron: видео под фильтром");
        int firstSection = arguments.FindIndex(a => a.StartsWith("--name=", StringComparison.Ordinal) && preset.Sections.Any(s => s.RawArguments.Contains(a)));

        Assert.True(window >= 0 && window < firstSection);
        Assert.Contains($"--lua-init=@lua/{VideoWindow.Module}", arguments);
        Assert.Contains("--wf-tcp-empty=1", arguments);
        Assert.Contains(arguments, a => a.StartsWith("--ipset-exclude-ip=") && a.Contains("173.194.0.0/16"));
    }

    [Fact]
    public void NoWindowWithoutList()
    {
        var arguments = WinwsCommandLine.Build(Preset()).ToList();

        Assert.DoesNotContain("--lua-desync=zy_video_window", arguments);
        Assert.DoesNotContain("--wf-tcp-empty=1", arguments);
    }
}
