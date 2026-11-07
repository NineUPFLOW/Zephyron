using Zephyron.Core.Rules;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>Путь туннеля «VPN / WARP» поверх прежних полей (07.11).</summary>
public sealed class TunnelPathTests
{
    private const string Sub = "https://panel.example/sub";

    [Theory]
    [InlineData(OperatingMode.DesyncOnly, false, null, TunnelPath.Vpn)]
    [InlineData(OperatingMode.Selective, true, null, TunnelPath.Warp)]
    [InlineData(OperatingMode.Selective, false, Sub, TunnelPath.Vpn)]
    [InlineData(OperatingMode.ProxyAll, true, Sub, TunnelPath.Warp)]
    [InlineData(OperatingMode.ProxyAll, false, Sub, TunnelPath.Vpn)]
    public void Old_settings_are_read_by_the_warp_flag(OperatingMode mode, bool warp, string? sub, TunnelPath path)
    {
        var settings = new AppSettings { Mode = mode, WarpEnabled = warp, SubscriptionUrl = sub };

        Assert.Equal(path, TunnelPaths.Of(settings));
    }

    [Fact]
    public void Apply_writes_only_the_path_and_keeps_the_mode()
    {
        var hybrid = new AppSettings { Mode = OperatingMode.Selective, SubscriptionUrl = Sub };

        var warp = TunnelPaths.Apply(hybrid, TunnelPath.Warp);
        Assert.True(warp.WarpEnabled);
        Assert.Equal(OperatingMode.Selective, warp.Mode);
        Assert.Equal(WorkMode.Route, WorkModes.Of(warp.Engines));

        var back = TunnelPaths.Apply(warp, TunnelPath.Vpn);
        Assert.False(back.WarpEnabled);
        Assert.Equal(TunnelPath.Vpn, TunnelPaths.Of(back));
    }

    [Fact]
    public void Vpn_without_subscriptions_is_not_applied()
    {
        var warp = new AppSettings { WarpEnabled = true };

        Assert.False(TunnelPaths.CanChoose(warp, TunnelPath.Vpn));
        Assert.Same(warp, TunnelPaths.Apply(warp, TunnelPath.Vpn));
        Assert.True(TunnelPaths.CanChoose(new AppSettings(), TunnelPath.Warp));
        Assert.True(TunnelPaths.CanChoose(new AppSettings { KeysEnabled = true }, TunnelPath.Vpn));
    }

    [Fact]
    public void Names_via_vpn_get_desync_in_the_desync_mode()
    {
        var desync = WorkModes.Choose(new AppSettings { SubscriptionUrl = Sub }, WorkMode.Desync);

        Assert.Equal(RoutingMode.Desync, desync.Engines.Effective(RoutingMode.Proxy, tunnelUp: desync.NeedsProxy));
        Assert.Equal(RoutingMode.Direct, desync.Engines.Effective(RoutingMode.Direct, tunnelUp: desync.NeedsProxy));
    }
}
