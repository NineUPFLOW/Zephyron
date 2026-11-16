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

/// <summary>Какие имена считаются YouTube для пробы браузерным приветствием.</summary>
public sealed class YouTubeHostTests
{
    [Theory]
    [InlineData("www.youtube.com", true)]
    [InlineData("rr1---sn-npcpgpm-3w5e.googlevideo.com", true)]
    [InlineData("i.ytimg.com", true)]
    [InlineData("youtu.be", true)]
    [InlineData("notyoutube.com", false)]
    [InlineData("google.com", false)]
    public void YouTubeNamesAreKnown(string host, bool expected) =>
        Assert.Equal(expected, BlockCheck.IsYouTube(host));
}
