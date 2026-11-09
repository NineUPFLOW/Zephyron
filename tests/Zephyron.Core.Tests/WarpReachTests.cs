using Xunit;
using Zephyron.Proxy;

namespace Zephyron.Core.Tests;

public sealed class WarpReachTests
{
    private static ServerHealth Health(DateTimeOffset at, params bool[] recent) => new()
    {
        Tag = "Cloudflare WARP", Success = recent.Length > 0 && recent[^1], CheckedAt = at, Recent = recent,
    };

    [Fact]
    public void Blocked_only_when_fresh_and_never_answered()
    {
        var now = DateTimeOffset.Now;
        Assert.True(WarpReach.LooksBlocked(Health(now, false, false, false), now));
        Assert.False(WarpReach.LooksBlocked(Health(now, false, false), now));
        Assert.False(WarpReach.LooksBlocked(Health(now, false, true, false), now));
        Assert.False(WarpReach.LooksBlocked(Health(now.AddHours(-3), false, false, false), now));
        Assert.False(WarpReach.LooksBlocked(null, now));
    }

    [Fact]
    public void Recent_exits_skip_warp()
    {
        var path = Path.Combine(Path.GetTempPath(), "eh-" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            ExitHistory.Note("DE-1", DateTimeOffset.Now, path);
            ExitHistory.Note("Cloudflare WARP", DateTimeOffset.Now, path);
            Assert.Equal(["DE-1"], ExitHistory.Recent(5, path).Select(e => e.Tag));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
