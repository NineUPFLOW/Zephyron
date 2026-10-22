using Zephyron.Proxy;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>
/// Правила автоподбора, общие для конфига движка и вкладки VPN.
/// </summary>
/// <remarks>
/// Вкладка по ним объясняет, почему сервер «вне подбора» (жалоба 22.10:
/// «почему он подключает сервер с 200 пингом, а не с 100»). Разойдись они
/// с движком — метка говорила бы одно, а выбирал бы он другое.
/// </remarks>
public sealed class AutoPickRulesTests
{
    [Fact]
    public void Only_the_fastest_per_entry_stay()
    {
        var servers = new[]
        {
            (Tag: "a", Entry: "1.2.3.4:443", Ms: 210.0),
            (Tag: "b", Entry: "1.2.3.4:443", Ms: 95.0),
            (Tag: "c", Entry: "1.2.3.4:443", Ms: 120.0),
            (Tag: "d", Entry: "5.6.7.8:443", Ms: 300.0),
        };

        var keep = SingBoxConfigCompiler.KeepPerEntry(servers, s => s.Entry, s => s.Ms, 2);

        Assert.Equal(["b", "c", "d"], keep.Select(s => s.Tag).Order());
    }

    [Fact]
    public void Zero_means_no_cap()
    {
        var servers = new[] { (Entry: "x:1", Ms: 1.0), (Entry: "x:1", Ms: 2.0), (Entry: "x:1", Ms: 3.0) };

        Assert.Equal(3, SingBoxConfigCompiler.KeepPerEntry(servers, s => s.Entry, s => s.Ms, 0).Count);
    }

    [Fact]
    public void An_entry_is_host_and_port_regardless_of_case()
    {
        Assert.Equal(SingBoxConfigCompiler.EntryOf("Vpn.Example.COM ", 443), SingBoxConfigCompiler.EntryOf("vpn.example.com", 443));
        Assert.NotEqual(SingBoxConfigCompiler.EntryOf("vpn.example.com", 443), SingBoxConfigCompiler.EntryOf("vpn.example.com", 8443));
    }

    [Theory]
    [InlineData("🇷🇺 Москва", true)]
    [InlineData("Россия — TLS", true)]
    [InlineData("Russia 2", true)]
    [InlineData("🇩🇪 Германия", false)]
    public void Domestic_exits_are_named_so(string tag, bool domestic) =>
        Assert.Equal(domestic, SingBoxConfigCompiler.LooksDomestic(tag));
}
