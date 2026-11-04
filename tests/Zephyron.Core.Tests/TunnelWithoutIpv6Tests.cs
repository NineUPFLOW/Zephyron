using System.Text.Json;
using Zephyron.Core.Diagnostics;
using Zephyron.Core.Rules;
using Zephyron.Proxy;
using Zephyron.Subscriptions;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>Туннель без IPv6 в Windows и сокет одного семейства у WireGuard.</summary>
public sealed class TunnelWithoutIpv6Tests
{
    private const string Wg =
        "wireguard://K1mwfCsDrWYcuV34G7RXrAiJoatFKtI6/NPLfwCavVQ=@wg.example.com:51820"
        + "?publickey=0e6a0OqdsnKPvxM3rX%2FzImqcLAMx0J9xPni%2FhixPltk%3D&address=10.0.0.2#WG";

    private static JsonElement Compile(SingBoxOptions options, params string[] links)
    {
        var servers = links.Select(l => ProxyUriParser.TryParse(l, out var s, out var e) ? s! : throw new Exception(e)).ToList();
        var engine = RuleSetLoader.Load("mode: selective\nrules: []\n");
        var result = new SingBoxConfigCompiler().Compile(engine.RuleSet, servers, options);
        var check = SingBoxCheck.Run(result.Json);
        Assert.True(check.Ok, check.Said);
        return JsonDocument.Parse(result.Json).RootElement;
    }

    [Fact]
    public void WithoutIpv6TheTunIsIpv4Only()
    {
        var root = Compile(new SingBoxOptions
        {
            Scope = TunnelScope.ProxyOnly,
            DnsServerAddresses = ["192.168.1.1/32", "fec0:0:0:ffff::1/128"],
            Ipv6 = false,
        });

        var tun = root.GetProperty("inbounds").EnumerateArray().Single(i => i.GetProperty("type").GetString() == "tun");
        Assert.Equal(["172.19.0.1/30"], tun.GetProperty("address").EnumerateArray().Select(a => a.GetString()));

        var routed = tun.GetProperty("route_address").EnumerateArray().Select(a => a.GetString()!).ToList();
        Assert.Contains("198.18.0.0/15", routed);
        Assert.Contains("192.168.1.1/32", routed);
        Assert.DoesNotContain(routed, c => c.Contains(':'));

        var fake = root.GetProperty("dns").GetProperty("servers").EnumerateArray().Single(s => s.GetProperty("tag").GetString() == "fake");
        Assert.False(fake.TryGetProperty("inet6_range", out _));
    }

    [Fact]
    public void WithIpv6TheTunKeepsBothFamilies()
    {
        var root = Compile(new SingBoxOptions { Scope = TunnelScope.ProxyOnly });
        var tun = root.GetProperty("inbounds").EnumerateArray().Single(i => i.GetProperty("type").GetString() == "tun");

        Assert.Equal(2, tun.GetProperty("address").GetArrayLength());
        Assert.Contains("fc00::/18", tun.GetProperty("route_address").EnumerateArray().Select(a => a.GetString()));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0xFF, true)]
    [InlineData(0x10, true)]
    [InlineData(0x11, true)]
    [InlineData(0x01, false)]
    [InlineData(0x20, false)]
    [InlineData(0, false)]
    public void DisabledComponentsIsReadByTheNonTunnelBit(int value, bool disabled) =>
        Assert.Equal(disabled, WindowsIpv6.Disables(value));

    [Fact]
    public void NoDisabledComponentsMeansIpv6IsThere()
    {
        Assert.False(WindowsIpv6.Disables(null));
        Assert.False(WindowsIpv6.Disables("0xff"));
    }

    [Fact]
    public void WireGuardGoesOutThroughOneFamilySocket()
    {
        var root = Compile(new SingBoxOptions(), Wg);
        var endpoint = root.GetProperty("endpoints").EnumerateArray().Single();

        Assert.Equal("direct", endpoint.GetProperty("detour").GetString());
    }
}
