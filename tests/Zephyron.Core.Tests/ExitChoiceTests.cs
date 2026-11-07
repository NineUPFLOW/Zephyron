using Zephyron.Core.Rules;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>Выход «Нет / WARP / Мой VPN» и «весь трафик» поверх прежних полей (07.11).</summary>
public sealed class ExitChoiceTests
{
    private const string Sub = "https://panel.example/sub";

    private static AppSettings Old(OperatingMode mode, bool warp = false, string? sub = null) =>
        new() { Mode = mode, WarpEnabled = warp, SubscriptionUrl = sub };

    [Theory]
    [InlineData(OperatingMode.DesyncOnly, false, null, ExitChoice.None, false)]
    [InlineData(OperatingMode.DesyncOnly, true, Sub, ExitChoice.None, false)]
    [InlineData(OperatingMode.Selective, true, null, ExitChoice.Warp, false)]
    [InlineData(OperatingMode.Selective, false, Sub, ExitChoice.Vpn, false)]
    [InlineData(OperatingMode.ProxyAll, true, Sub, ExitChoice.Warp, true)]
    [InlineData(OperatingMode.ProxyAll, false, Sub, ExitChoice.Vpn, true)]
    [InlineData(OperatingMode.ProxyStrict, false, Sub, ExitChoice.Vpn, true)]
    [InlineData(OperatingMode.Selective, false, null, ExitChoice.None, false)]
    public void Old_settings_are_read_without_surprises(OperatingMode mode, bool warp, string? sub, ExitChoice exit, bool all)
    {
        var settings = Old(mode, warp, sub);

        Assert.Equal(exit, Exits.Of(settings));
        Assert.Equal(all, Exits.AllTraffic(settings));
    }

    [Fact]
    public void Apply_sets_the_fields_consistently()
    {
        var start = Old(OperatingMode.DesyncOnly, sub: Sub);

        var warp = Exits.Apply(start, ExitChoice.Warp, allTraffic: false);
        Assert.True(warp.WarpEnabled);
        Assert.Equal((true, true), (warp.Engines.Desync, warp.Engines.Tunnel));
        Assert.Equal(OperatingMode.Selective, warp.Mode);

        var all = Exits.Apply(warp, ExitChoice.Warp, allTraffic: true);
        Assert.Equal((false, true), (all.Engines.Desync, all.Engines.Tunnel));
        Assert.Equal(OperatingMode.ProxyAll, all.Mode);
        Assert.Equal(ExitChoice.Warp, Exits.Of(all));

        var vpn = Exits.Apply(all, ExitChoice.Vpn);
        Assert.False(vpn.WarpEnabled);
        Assert.True(Exits.AllTraffic(vpn));
        Assert.Equal(ExitChoice.Vpn, Exits.Of(vpn));

        var none = Exits.Apply(vpn, ExitChoice.None);
        Assert.False(none.WarpEnabled);
        Assert.Equal((true, false), (none.Engines.Desync, none.Engines.Tunnel));
        Assert.Equal(OperatingMode.DesyncOnly, none.Mode);
        Assert.False(Exits.AllTraffic(none));
    }

    [Fact]
    public void My_vpn_without_subscriptions_is_not_applied()
    {
        var bare = new AppSettings();

        Assert.False(Exits.CanChoose(bare, ExitChoice.Vpn));
        Assert.Same(bare, Exits.Apply(bare, ExitChoice.Vpn));
        Assert.True(Exits.CanChoose(bare, ExitChoice.Warp));
        Assert.True(Exits.CanChoose(new AppSettings { KeysEnabled = true }, ExitChoice.Vpn));
    }

    [Fact]
    public void Ignoring_exclusions_survives_all_traffic_and_drops_with_it()
    {
        var strict = Old(OperatingMode.ProxyStrict, sub: Sub);

        Assert.True(Exits.IgnoresExclusions(strict));
        Assert.True(Exits.Apply(strict, ExitChoice.Vpn, allTraffic: true).Engines.IgnoreExclusions);

        var routes = Exits.Apply(strict, ExitChoice.Vpn, allTraffic: false);
        Assert.False(routes.Engines.IgnoreExclusions);
        Assert.False(Exits.AllTraffic(routes));
    }

    [Fact]
    public void Names_via_exit_get_desync_when_no_exit_is_chosen()
    {
        var none = Exits.Apply(Old(OperatingMode.Selective, sub: Sub), ExitChoice.None);

        Assert.Equal(RoutingMode.Desync, none.Engines.Effective(RoutingMode.Proxy, tunnelUp: none.NeedsProxy));
        Assert.Equal(RoutingMode.Direct, none.Engines.Effective(RoutingMode.Direct, tunnelUp: none.NeedsProxy));
    }
}
