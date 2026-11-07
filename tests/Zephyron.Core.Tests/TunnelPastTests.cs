using System.Net;
using Zephyron.Core.Connections;
using Zephyron.Core.Rules;
using Zephyron.Proxy;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>Имя «через VPN» с настоящим адресом: наблюдение и проверка имён.</summary>
public sealed class TunnelPastTests
{
    private static readonly string[] Include = ["104.16.0.0/12", "198.18.0.0/15"];
    private static readonly string[] Exclude = ["104.17.0.0/16"];

    private static WatchContext Context(bool selective = true) => new(
        ["198.18.0.0/15"], [], selective ? Include : null, selective ? Exclude : null);

    [Theory]
    [InlineData("104.16.5.5", false)]   // в забираемой сети
    [InlineData("188.40.167.81", true)] // вне забираемых
    [InlineData("104.17.1.1", true)]    // в выведенной
    public void RealAddressOutsideTheRoutesBypassesTheTunnel(string address, bool bypass) =>
        Assert.Equal(bypass, Context().BypassesTunnel(IPAddress.Parse(address)));

    [Fact]
    public void WithoutRoutesNothingBypasses() =>
        Assert.False(Context(selective: false).BypassesTunnel(IPAddress.Parse("188.40.167.81")));

    private static WatchEntry Entry(string address, WatchContext context)
    {
        var connection = new ConnectionEvent
        {
            Timestamp = DateTimeOffset.Now,
            Protocol = ProtocolKind.Tcp,
            RemoteAddress = IPAddress.Parse(address),
            RemotePort = 443,
            ExecutablePath = @"C:\x\app.exe",
            Hostname = "2ip.io",
        };

        var rule = new RoutingRule { Match = MatchKind.Domain, Value = "2ip.io", Mode = RoutingMode.Proxy, Ordinal = 7 };

        return WatchEntry.From(connection, new RuleDecision { Mode = RoutingMode.Proxy, Rule = rule, Reason = "domain 2ip.io" }, context);
    }

    [Fact]
    public void ProxyRuleWithARealOutsideAddressIsShownAsDirect()
    {
        var entry = Entry("188.40.167.81", Context());

        Assert.Equal(WatchRoute.Direct, entry.Mode);
        Assert.True(entry.IsBypass);
        Assert.True(entry.Certain);
        Assert.StartsWith("мимо туннеля · #7", entry.Rule);
        Assert.StartsWith("мимо туннеля · ", entry.RuleName);

        var back = WatchEntry.Parse(entry.ToLine())!;
        Assert.True(back.IsBypass);
        Assert.Contains("Мимо туннеля: 1", WatchEntry.Summarize([entry.ToLine()]));
    }

    [Fact]
    public void InsideRoutesOrFakeStaysTunnel()
    {
        Assert.Equal(WatchRoute.Proxy, Entry("104.16.5.5", Context()).Mode);
        Assert.Equal(WatchRoute.Proxy, Entry("198.18.0.9", Context()).Mode);
        Assert.False(Entry("188.40.167.81", Context(selective: false)).IsBypass);
    }

    private static IReadOnlyList<IPAddress> Ips(params string[] a) => [.. a.Select(IPAddress.Parse)];

    [Fact]
    public void FakeAnswersPass()
    {
        var v = TunnelNameCheck.Judge([("2ip.io", Ips("198.18.0.4"))], null, []);
        Assert.True(v.Ok);
    }

    [Fact]
    public void RealAnswerFailsAndSaysWhatToDo()
    {
        var v = TunnelNameCheck.Judge([("a.io", Ips("198.18.0.4")), ("2ip.io", Ips("188.40.167.81"))], null, ["192.168.1.1/32"]);

        Assert.False(v.Ok);
        Assert.Contains("2ip.io → 188.40.167.81", v.Text);
        Assert.DoesNotContain("a.io →", v.Text);
        Assert.Contains("192.168.1.1", v.Text);
        Assert.Contains("сторонний DNS-клиент", v.Text);
    }

    [Fact]
    public void NamesSkipPinsMasksAndNonProxyAndStopAtThree()
    {
        RoutingRule R(string v, RoutingMode m = RoutingMode.Proxy) => new() { Match = MatchKind.Domain, Value = v, Mode = m };

        var rules = new RuleSet
        {
            DefaultMode = RoutingMode.Desync,
            Rules = [R("*.x.com"), R("pinned.io"), R("direct.io", RoutingMode.Direct), R("a.io"), R("b.io"), R("c.io"), R("d.io")],
        };

        Assert.Equal(["a.io", "b.io", "c.io"], TunnelNameCheck.Names(rules, ["pinned.io"]));
    }
}
