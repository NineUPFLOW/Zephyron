using Xunit;
using Zephyron.Subscriptions;

namespace Zephyron.Core.Tests;

public class WarpFileTests
{
    private const string Key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    private static readonly string Conf = $"""
        [Interface]
        PrivateKey = {Key}
        Address = 172.16.0.2
        Jc = 4
        Jmin = 40
        Jmax = 70

        [Peer]
        PublicKey = {Key}
        AllowedIPs = 0.0.0.0/0
        Endpoint = 162.159.192.1:2408
        """;

    [Fact]
    public void Wireguard_file_becomes_the_warp_exit()
    {
        Assert.True(Warp.Parse(Conf, out var server, out _));
        Assert.Equal(Warp.MasqueTag, server!.Tag);
        Assert.Equal(ProxyProtocol.Wireguard, server.Protocol);
        Assert.False(server.IsSelfRegistering);
    }

    [Fact]
    public void Vpn_key_is_not_a_warp_file()
    {
        Assert.False(Warp.Parse("vless://11111111-1111-1111-1111-111111111111@example.com:443?security=tls#x", out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Missing_file_leaves_builtin_warp()
    {
        Assert.Null(Warp.FileServer(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".conf")));
    }

    [Fact]
    public void Saved_file_is_read_back()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".conf");

        try
        {
            File.WriteAllText(path, Conf);
            Assert.Equal(Warp.MasqueTag, Warp.FileServer(path)!.Tag);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
