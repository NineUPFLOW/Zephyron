using System.Net;
using Xunit;
using Zephyron.Core.Diagnostics;

namespace Zephyron.Core.Tests;

public sealed class SystemDnsCheckTests
{
    private static readonly IPAddress[] Real = [IPAddress.Parse("142.250.74.110")];

    [Fact]
    public void Missing_blocked_name_while_control_resolves_is_poisoning() =>
        Assert.True(SystemDnsCheck.Poisoned(name => name == "www.youtube.com" ? null : Real));

    [Fact]
    public void Stub_address_counts_as_poisoning() =>
        Assert.True(SystemDnsCheck.Poisoned(name => name == "rutracker.org" ? [IPAddress.Parse("0.0.0.0")] : Real));

    [Fact]
    public void Everything_resolving_is_not_poisoning() =>
        Assert.False(SystemDnsCheck.Poisoned(_ => Real));

    [Fact]
    public void Dead_network_is_not_poisoning() =>
        Assert.False(SystemDnsCheck.Poisoned(_ => null));
}
