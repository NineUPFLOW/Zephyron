using Xunit;
using Zephyron.Zapret;

namespace Zephyron.Core.Tests;

/// <summary>Чья сеть у адреса — по спискам адресов Zapret.</summary>
public sealed class AddressOwnersTests
{
    [Fact]
    public void Owner_is_the_first_list_that_holds_the_address()
    {
        var owners = AddressOwners.From(
            ("Telegram", ["149.154.160.0/20", "2001:b28:f23d::/48"]),
            ("Облако", ["149.154.0.0/16", "10.0.0.0/8", "10.1.0.0/16", "10.2.3.4/32"]));

        Assert.Equal("Telegram", owners.Of("149.154.167.220"));
        Assert.Equal("Облако", owners.Of("149.154.1.1"));
        Assert.Equal("Облако", owners.Of("10.1.2.3"));
        Assert.Equal("Облако", owners.Of("10.255.255.255"));
        Assert.Equal("Telegram", owners.Of("2001:b28:f23d::1"));
        Assert.Null(owners.Of("11.0.0.0"));
        Assert.Null(owners.Of("не адрес"));
        Assert.Null(owners.Of(null));
    }

    [Fact]
    public void Real_lists_name_the_network_right()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "config", "rules.yaml")))
            dir = dir.Parent;

        var owners = AddressOwners.Load(Path.Combine(dir!.FullName, "vendor", "zapret-gui"));

        Assert.Equal("Cloudflare", owners.Of("104.16.132.229"));
        Assert.Equal("Telegram", owners.Of("149.154.167.220"));
        Assert.Null(owners.Of("192.168.1.1"));
    }
}
