using Xunit;
using Zephyron.Subscriptions;

namespace Zephyron.Core.Tests;

public sealed class SubscriptionTrustTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
    private const string Id = "b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33";

    private static ProxyServer Server(string tag, string host, string id = Id, ushort port = 443) => new()
    {
        Protocol = ProxyProtocol.Vless, Tag = tag, Host = host, Port = port, Credential = id, Security = "tls",
    };

    private static SubscriptionInfo Info(IEnumerable<ProxyServer> servers, DateTimeOffset? expires = null, long total = 0, long used = 0) =>
        new() { Servers = [.. servers], ExpiresAt = expires, TotalBytes = total, DownloadBytes = used };

    [Fact]
    public void Healthy_subscription_is_ok()
    {
        var v = SubscriptionTrust.Assess(Info([Server("DE", "de.vpn.net"), Server("NL", "nl.vpn.net")], Now.AddDays(30), 100, 5), Now);
        Assert.Equal(TrustLevel.Ok, v.Level);
        Assert.Empty(v.Reasons);
    }

    [Theory]
    [InlineData("Истекла подписка", "de.vpn.net")]
    [InlineData("Продлите на сайте", "de.vpn.net")]
    [InlineData("Telegram @vpnbot", "de.vpn.net")]
    [InlineData("DE", "node.invalid")]
    public void Placeholder_servers_are_pseudo_and_not_usable(string tag, string host)
    {
        var s = Server(tag, host);
        Assert.True(s.IsPseudo);
        Assert.False(s.IsUsableOutbound);
    }

    [Fact]
    public void Example_hosts_make_the_subscription_fake_but_servers_stay_usable()
    {
        var s = Server("DE", "example.com");
        Assert.True(s.IsUsableOutbound);
        Assert.Equal(TrustLevel.Fake, SubscriptionTrust.Assess(Info([s]), Now).Level);
    }

    [Fact]
    public void Info_rows_are_marked_but_real_servers_keep_the_subscription_alive()
    {
        var v = SubscriptionTrust.Assess(Info([Server("Истекла", "0.0.0.1"), Server("DE", "de.vpn.net")]), Now);
        Assert.Equal(TrustLevel.Suspicious, v.Level);
        Assert.Single(v.PseudoServers);
    }

    [Fact]
    public void All_pseudo_is_fake()
    {
        var v = SubscriptionTrust.Assess(Info([Server("A", "127.0.0.1", port: 1), Server("B", "localhost", port: 0)]), Now);
        Assert.Equal(TrustLevel.Fake, v.Level);
    }

    [Fact]
    public void Same_server_repeated_with_expired_term_is_fake()
    {
        var v = SubscriptionTrust.Assess(Info([Server("A", "x.net"), Server("B", "x.net")], Now.AddDays(-1)), Now);
        Assert.Equal(TrustLevel.Fake, v.Level);
    }

    [Fact]
    public void Expired_or_exhausted_alone_is_suspicious_and_zero_total_is_unlimited()
    {
        var servers = new[] { Server("DE", "de.vpn.net"), Server("NL", "nl.vpn.net") };
        Assert.Equal(TrustLevel.Suspicious, SubscriptionTrust.Assess(Info(servers, Now.AddDays(-1)), Now).Level);
        Assert.Equal(TrustLevel.Suspicious, SubscriptionTrust.Assess(Info(servers, null, 10, 10), Now).Level);
        Assert.Equal(TrustLevel.Ok, SubscriptionTrust.Assess(Info(servers, null, 0, 999), Now).Level);
    }

    [Fact]
    public void Loopback_stub_port_is_pseudo_but_local_chain_is_not()
    {
        Assert.True(Server("DE", "127.0.0.1", port: 1).IsPseudo);
        Assert.False(Server("local xray", "127.0.0.1", port: 10808).IsPseudo);
    }

    [Fact]
    public void Local_socks_chain_is_not_pseudo()
    {
        var s = new ProxyServer { Protocol = ProxyProtocol.Socks, Tag = "local", Host = "127.0.0.1", Port = 1080, Credential = "" };
        Assert.False(s.IsPseudo);
    }
}
