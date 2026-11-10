using Zephyron.Proxy;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>Страж узлов видео: какие имена его, сроки отказов, состояние и снятие только своих записей.</summary>
public sealed class VideoNodeGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "video-guard-" + Guid.NewGuid().ToString("N"));

    public VideoNodeGuardTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Hosts, "127.0.0.1 localhost\r\n");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Hosts => Path.Combine(_dir, "hosts");

    private string State => Path.Combine(_dir, "video-guard.json");

    [Theory]
    [InlineData("rr2---sn-4g5lznse.googlevideo.com", true)]
    [InlineData("RR1---SN-8PH2XAJVH-NUFS.GOOGLEVIDEO.COM", true)]
    [InlineData("redirector.googlevideo.com", false)]
    [InlineData("rr2---sn-4g5lznse.googlevideo.com.evil.net", false)]
    [InlineData("www.youtube.com", false)]
    public void KnowsVideoNodes(string host, bool expected) => Assert.Equal(expected, VideoNodeGuard.IsVideoNode(host));

    [Fact]
    public void RechecksAfterHalfHourAndDropsAfterTwelveHours()
    {
        var now = DateTimeOffset.Now;
        var state = new Dictionary<string, VideoNodeGuard.Entry>
        {
            ["fresh"] = new("1.1.1.1", now.AddMinutes(-5), now.AddMinutes(-5)),
            ["stale"] = new("1.1.1.2", now.AddHours(-2), now.AddMinutes(-31)),
            ["old"] = new("1.1.1.3", now.AddHours(-13), now.AddMinutes(-1)),
        };

        var (recheck, expired) = VideoNodeGuard.Due(state, now);

        Assert.Equal(["stale"], recheck);
        Assert.Equal(["old"], expired);
    }

    [Fact]
    public void StateSurvivesSaveAndBrokenFile()
    {
        var at = DateTimeOffset.Now;
        VideoNodeGuard.Save(new Dictionary<string, VideoNodeGuard.Entry> { ["a"] = new("1.2.3.4", at, at) }, State);

        var loaded = VideoNodeGuard.Load(State);
        Assert.Equal("1.2.3.4", loaded["A"].Address);

        File.WriteAllText(State, "{не json");
        Assert.Empty(VideoNodeGuard.Load(State));
    }

    [Fact]
    public void ClearRemovesOnlyOwnRefusals()
    {
        HostsEditor.Pin(new Dictionary<string, string> { ["rr2---sn-4g5lznse.googlevideo.com"] = VideoNodeGuard.Refused },
            Hosts, "test", new Dictionary<string, string> { ["rr2---sn-4g5lznse.googlevideo.com"] = VideoNodeGuard.Via });
        HostsEditor.Pin(new Dictionary<string, string> { ["claude.ai"] = "1.2.3.4" }, Hosts, "test");

        Assert.Equal(1, VideoNodeGuard.Clear(Hosts, State));

        var text = File.ReadAllText(Hosts);
        Assert.DoesNotContain("googlevideo", text);
        Assert.Contains("claude.ai", text);
    }
}
