using System.Text.Json;
using Xunit;
using Zephyron.Core.Rules;
using Zephyron.Proxy;
using Zephyron.Subscriptions;

namespace Zephyron.Core.Tests;

public class LocalProxyTests
{
    private static ProxyServer Server(string tag) => new()
    {
        Protocol = ProxyProtocol.Hysteria2,
        Tag = tag,
        Host = tag + ".example.com",
        Port = 4443,
        Credential = "PLACEHOLDER",
        Transport = "udp",
        Security = "tls",
        Sni = tag + ".example.com",
    };

    private static JsonElement Compile(bool proxy, params ProxyServer[] servers)
    {
        var rules = RuleSetLoader.Load("rules: []\n").RuleSet;
        var result = new SingBoxConfigCompiler().Compile(rules, servers, new SingBoxOptions
        {
            LocalProxy = proxy,
            LocalProxyPort = 2080,
        });

        return JsonDocument.Parse(result.Json).RootElement.Clone();
    }

    [Fact]
    public void AddsLoopbackInboundWithoutAuth()
    {
        var inbound = Compile(true, Server("NL")).GetProperty("inbounds").EnumerateArray()
            .Single(i => i.GetProperty("tag").GetString() == "local-proxy");

        Assert.Equal("mixed", inbound.GetProperty("type").GetString());
        Assert.Equal("127.0.0.1", inbound.GetProperty("listen").GetString());
        Assert.Equal(2080, inbound.GetProperty("listen_port").GetInt32());
        Assert.False(inbound.TryGetProperty("users", out _));
    }

    [Fact]
    public void ExitGroupHasServersButNoDirect()
    {
        var group = Compile(true, Server("NL"), Server("DE")).GetProperty("outbounds").EnumerateArray()
            .Single(o => o.GetProperty("tag").GetString() == "proxy-exit");
        var members = group.GetProperty("outbounds").EnumerateArray().Select(m => m.GetString()).ToList();

        Assert.Contains("NL", members);
        Assert.Contains("DE", members);
        Assert.DoesNotContain("direct", members);
    }

    [Fact]
    public void RuleGoesRightAfterPrivateNetworks()
    {
        var rules = Compile(true, Server("NL")).GetProperty("route").GetProperty("rules").EnumerateArray().ToList();
        int priv = rules.FindIndex(r => r.TryGetProperty("ip_is_private", out _));
        int mine = rules.FindIndex(r => r.TryGetProperty("inbound", out var i) && i[0].GetString() == "local-proxy");

        Assert.True(priv >= 0);
        Assert.Equal(priv + 1, mine);
        Assert.Equal("proxy-exit", rules[mine].GetProperty("outbound").GetString());
    }

    [Fact]
    public void NoServersMeansNoInbound()
    {
        var root = Compile(true);

        Assert.DoesNotContain(root.GetProperty("inbounds").EnumerateArray(),
            i => i.GetProperty("tag").GetString() == "local-proxy");
        Assert.DoesNotContain("proxy-exit", root.GetProperty("outbounds").GetRawText());
    }

    [Fact]
    public void OffByDefault()
    {
        Assert.DoesNotContain("local-proxy", Compile(false, Server("NL")).GetRawText());
    }

    [Fact]
    public void PortOwnedByRunningConfigIsNotAProblem()
    {
        var path = Path.GetTempFileName();

        try
        {
            File.WriteAllText(path, """{"inbounds":[{"tag":"local-proxy","listen_port":2080}]}""");

            Assert.Null(LocalProxyInbound.Problem(2080, path, busy: _ => true, engineRunning: () => true));
            // Конфиг остался от прошлого запуска, движок не работает — порт держит кто-то другой.
            Assert.NotNull(LocalProxyInbound.Problem(2080, path, busy: _ => true, engineRunning: () => false));
            Assert.NotNull(LocalProxyInbound.Problem(2081, path, busy: _ => true));

            File.WriteAllText(path, """{"inbounds":[{"tag":"health-in","listen_port":2080}]}""");

            Assert.NotNull(LocalProxyInbound.Problem(2080, path, busy: _ => true));
            Assert.Null(LocalProxyInbound.Problem(2080, path, busy: _ => false));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReservedAndOutOfRangePortsAreRejected()
    {
        Assert.False(LocalProxyInbound.PortAllowed(80));
        Assert.False(LocalProxyInbound.PortAllowed(9090));
        Assert.False(LocalProxyInbound.PortAllowed(70000));
        Assert.True(LocalProxyInbound.PortAllowed(2080));
    }
}
