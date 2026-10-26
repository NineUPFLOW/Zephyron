using System.Net;
using Zephyron.Supervisor;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>Брошенный адаптер — только наш: чужой sing-tun не трогаем.</summary>
public sealed class GhostTunnelTests
{
    private static readonly IPAddress Ours = IPAddress.Parse("172.19.0.1");
    private static readonly IPAddress Other = IPAddress.Parse("172.18.0.1");

    [Fact]
    public void OurNameIsOurs() =>
        Assert.True(GhostTunnel.IsOurs("zephyron0", "sing-tun Tunnel", [Other]));

    /// <summary>Брошенный адаптер теряет имя, но держит адрес.</summary>
    [Fact]
    public void OurAddressUnderAnotherNameIsOurs() =>
        Assert.True(GhostTunnel.IsOurs("Ethernet 3", "sing-tun Tunnel #2", [Ours]));

    [Fact]
    public void AnotherClientsTunnelIsNotOurs() =>
        Assert.False(GhostTunnel.IsOurs("tun0", "sing-tun Tunnel", [Other]));

    [Fact]
    public void ARealAdapterWithOurNameIsNotOurs() =>
        Assert.False(GhostTunnel.IsOurs("zephyron0", "Intel(R) Ethernet", [Ours]));
}
