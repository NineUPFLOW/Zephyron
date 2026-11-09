using Xunit;
using Zephyron.Proxy;

namespace Zephyron.Core.Tests;

/// <summary>Шифрованный DNS через dnscrypt-proxy: режим, конфиг, резолвер движка.</summary>
public sealed class DnsCryptTests
{
    [Fact]
    public void Mode_keys_round_trip_and_unknown_is_off()
    {
        foreach (var mode in DnsCrypt.Modes)
            Assert.Equal(mode, DnsCrypt.Parse(DnsCrypt.Key(mode)));

        Assert.Null(DnsCrypt.Parse(null));
        Assert.Null(DnsCrypt.Parse("doh"));
        Assert.Null(new AppSettings().DnsCryptActive);
    }

    [Fact]
    public void Router_dns_wins_over_dnscrypt()
    {
        var settings = new AppSettings { DnsCrypt = "anonymized" };

        Assert.Equal(DnsCryptMode.Anonymized, settings.DnsCryptActive);
        Assert.Null((settings with { UseRouterDns = true }).DnsCryptActive);
    }

    [Fact]
    public void Config_per_mode_picks_servers_lists_and_port()
    {
        var dir = Path.GetTempPath();

        var plain = DnsCrypt.Config(DnsCryptMode.DnsCrypt, dir);
        Assert.Contains($"127.0.0.1:{DnsCrypt.Port}", plain);
        Assert.Contains("dnscrypt_servers = true", plain);
        Assert.DoesNotContain("anonymized_dns", plain);

        var odoh = DnsCrypt.Config(DnsCryptMode.Odoh, dir, port: 40000, cache: false);
        Assert.Contains("127.0.0.1:40000", odoh);
        Assert.Contains("odoh_servers = true", odoh);
        Assert.Contains("cache = false", odoh);
        Assert.Contains("[anonymized_dns]", odoh);
        Assert.Contains("odoh-relays", odoh);
    }

    [Fact]
    public void Engine_resolver_is_the_loopback_proxy_without_tunnel()
    {
        var settings = new AppSettings { DnsCrypt = "dnscrypt" };

        var result = DnsEngine.Compile(settings, ["8.8.8.8/32"], new Dictionary<string, string>(), EngineKeys.Generate());
        var text = result.Json;

        Assert.Contains($"\"server_port\": {DnsCrypt.Port}", text);
        Assert.Contains("127.0.0.1", text);
        Assert.Contains("dnscrypt-proxy.exe", text);
    }
}
