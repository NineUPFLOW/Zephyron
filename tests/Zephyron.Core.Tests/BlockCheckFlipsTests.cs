using Zephyron.Proxy;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>«С прошлой проверки»: только открылось и перестало, без смены одного «доступен» на другой.</summary>
public sealed class BlockCheckFlipsTests
{
    [Fact]
    public void OnlyRealFlipsCount()
    {
        var (opened, closed) = BlockCheckHistory.Flips(
        [
            new("discord.com", "—        DPI по TLS [сеть: Cloudflare]", "доступен [сеть: Cloudflare]"),
            new("firstlook.gg", "—        доступен [сеть: Cloudflare]", "доступен"),
            new("cdn.bsky.app", "—        доступен [сеть: CDN77]", "DPI по TLS"),
            new("x.com", "недоступен", "DPI по TLS"),
        ]);

        Assert.Equal(["discord.com"], opened);
        Assert.Equal(["cdn.bsky.app"], closed);
    }

    [Fact]
    public void LongListsAreCut() =>
        Assert.Equal("a, b и ещё 2", BlockCheckHistory.Names(["a", "b", "c", "d"], 2));
}
