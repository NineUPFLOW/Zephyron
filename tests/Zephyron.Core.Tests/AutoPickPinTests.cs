using Zephyron.Core.Rules;
using Zephyron.Proxy;
using Zephyron.Supervisor;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>
/// Пин в hosts как способ автоподбора: выбор, правило «пин работает», запись и откат.
/// </summary>
public sealed class AutoPickPinTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "autopick-pin-" + Guid.NewGuid().ToString("N"));

    public AutoPickPinTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Hosts, "127.0.0.1 localhost\r\n");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Rules => Path.Combine(_dir, "rules.user.yaml");

    private string Journal => Path.Combine(_dir, "journal.json");

    private string Hosts => Path.Combine(_dir, "hosts");

    private static readonly AutoPickTarget Site = new("netflix.com", "Netflix");

    private static AutoPickTry Try(AutoPickWay way, bool works, bool stub = false, int status = 200, double seconds = 1, string? recipe = null) =>
        new(way, recipe, recipe, way == AutoPickWay.Vpn ? "srv" : null, works, TimeSpan.FromSeconds(seconds), "",
            works || stub ? status : null, Stub: stub);

    private static AutoPickTry PinTry(bool works = true, int status = 200) =>
        new(AutoPickWay.Pin, null, "XBOX DNS", null, works, TimeSpan.FromSeconds(1), "", status, Address: "1.2.3.4");

    private static AutoPickRow Row(RoutingMode mode, params AutoPickTry[] tries)
    {
        var row = new AutoPickRow(Site, mode, null)
        {
            Pins = new Dictionary<string, string> { ["netflix.com"] = "1.2.3.4", ["www.netflix.com"] = "1.2.3.4" },
        };

        foreach (var attempt in tries)
            row.Add(attempt);

        return row;
    }

    private static PinProbe Probe(PinVerdict verdict, int? status, PinSource source = PinSource.Pool, string? colo = null) =>
        new(new PinCandidate("1.2.3.4", source, "XBOX DNS"), verdict, status, colo, TimeSpan.FromSeconds(1), "d");

    private static AutoPickReport Report(AutoPickRow row) =>
        new("p", [row], new Dictionary<string, TimeSpan>(), []);

    [Fact]
    public void Stable_pin_beats_stub_recipes_and_flaky_vpn()
    {
        var row = Row(RoutingMode.Direct,
            Try(AutoPickWay.Direct, true, stub: true, status: 403),
            Try(AutoPickWay.Recipe, true, stub: true, status: 403, recipe: "r"),
            PinTry(),
            PinTry(),
            Try(AutoPickWay.Vpn, true),
            Try(AutoPickWay.Vpn, false));

        var choice = AutoPick.Choose(row)!;

        Assert.Equal(AutoPickWay.Pin, choice.Way);
        Assert.Equal(RoutingMode.Direct, choice.Mode);
        Assert.Contains("netflix.com, www.netflix.com", choice.Why);
    }

    [Fact]
    public void Vpn_stays_when_nothing_else_opens()
    {
        var row = Row(RoutingMode.Direct,
            Try(AutoPickWay.Direct, false), Try(AutoPickWay.Recipe, false, recipe: "r"), PinTry(works: false),
            Try(AutoPickWay.Vpn, true), Try(AutoPickWay.Vpn, true));

        Assert.Equal(AutoPickWay.Vpn, AutoPick.Choose(row)!.Way);
    }

    [Fact]
    public void Single_pin_sample_is_repeated()
    {
        var row = Row(RoutingMode.Direct, Try(AutoPickWay.Direct, false), PinTry());
        row.Choice = AutoPick.Choose(row);

        Assert.Contains((AutoPickWay.Pin, (string?)null), AutoPick.NeedsRepeat(row));
    }

    [Theory]
    [InlineData(PinVerdict.Works, 200, PinSource.Pool, true)]
    [InlineData(PinVerdict.Works, 502, PinSource.Pool, false)]
    [InlineData(PinVerdict.Works, 200, PinSource.Honest, false)]
    [InlineData(PinVerdict.Challenge, 403, PinSource.Pool, false)]
    [InlineData(PinVerdict.Refused, 403, PinSource.Pool, false)]
    [InlineData(PinVerdict.Dead, null, PinSource.Pool, false)]
    public void Pin_works_rule(PinVerdict verdict, int? status, PinSource source, bool expected)
    {
        var probe = Probe(verdict, status, source);

        Assert.Equal(expected, AutoPick.PinWorks(probe));
        Assert.Equal(expected, AutoPick.PinTry(probe).Works);
    }

    [Fact]
    public void Challenge_outside_russia_points_to_manual_pin()
    {
        Assert.Contains("вручную", AutoPick.PinTry(Probe(PinVerdict.Challenge, 403, colo: "FRA")).Detail);
        Assert.DoesNotContain("вручную", AutoPick.PinTry(Probe(PinVerdict.Challenge, 403, colo: "DME")).Detail);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Working_vpn_is_offered_a_pin_instead(bool factory, bool optional)
    {
        var row = Row(RoutingMode.Proxy, Try(AutoPickWay.Direct, false), PinTry(), PinTry(), Try(AutoPickWay.Vpn, true));
        row.FactoryVpn = factory;

        var choice = AutoPick.Choose(row)!;

        Assert.Equal(AutoPickWay.Pin, choice.Way);
        Assert.Equal(optional, choice.Optional);
    }

    [Fact]
    public void Failed_pin_try_does_not_make_a_name_a_non_site()
    {
        var row = Row(RoutingMode.Direct, Try(AutoPickWay.Direct, false), PinTry(works: false, status: 502));

        Assert.Null(AutoPick.NotASite(row));
    }

    [Fact]
    public void Proxy_row_gets_a_direct_rule_and_a_pin()
    {
        var row = Row(RoutingMode.Proxy, Try(AutoPickWay.Vpn, true), PinTry(), PinTry());
        row.Choice = AutoPick.Choose(row);

        var outcome = AutoPick.Apply(Report(row), Rules, Journal, _ => (RoutingMode.Direct, null, null), Hosts);

        Assert.True(outcome.Single().Written);
        Assert.Contains(UserRulesFile.Load(Rules).Entries, e => e.Mode == RoutingMode.Direct && e.Matches("*.netflix.com"));
        Assert.Equal("1.2.3.4", HostsEditor.Pins(Hosts)["www.netflix.com"]);

        var entry = AutoPickJournal.Load(Journal).Single().Entries.Single();
        Assert.NotNull(entry.Wrote);
        Assert.Equal(2, entry.Pins!.Count);
    }

    [Fact]
    public void Non_proxy_row_is_only_pinned_and_restore_unpins()
    {
        var row = Row(RoutingMode.Direct, Try(AutoPickWay.Direct, false), PinTry(), PinTry());
        row.Choice = AutoPick.Choose(row);

        var outcome = AutoPick.Apply(Report(row), Rules, Journal, _ => (RoutingMode.Direct, null, null), Hosts);

        Assert.True(outcome.Single().Written);
        Assert.Empty(UserRulesFile.Load(Rules).Entries);

        var run = AutoPickJournal.Load(Journal).Single();
        Assert.Null(run.Entries.Single().Wrote);
        Assert.Equal("1.2.3.4", run.Entries.Single().Pins!["netflix.com"]);
        Assert.Equal(2, HostsEditor.Pins(Hosts).Count);

        Assert.Null(AutoPickJournal.RestoreAll(run, Rules, Journal, Hosts).Single());
        Assert.Empty(HostsEditor.Pins(Hosts));
        Assert.True(AutoPickJournal.Load(Journal).Single().Entries.Single().Restored);
    }

    [Fact]
    public void Pin_shadowed_by_a_vpn_rule_is_not_written()
    {
        var row = Row(RoutingMode.Direct, Try(AutoPickWay.Direct, false), PinTry(), PinTry());
        row.Choice = AutoPick.Choose(row);

        var outcome = AutoPick.Apply(Report(row), Rules, Journal, _ => (RoutingMode.Proxy, null, "правило"), Hosts);

        Assert.False(outcome.Single().Written);
        Assert.Empty(HostsEditor.Pins(Hosts));
    }

    [Fact]
    public void Changed_pin_is_left_alone_on_restore_and_so_is_the_rule()
    {
        var row = Row(RoutingMode.Proxy, Try(AutoPickWay.Vpn, true), PinTry(), PinTry());
        row.Choice = AutoPick.Choose(row);

        AutoPick.Apply(Report(row), Rules, Journal, _ => (RoutingMode.Direct, null, null), Hosts);
        HostsEditor.Pin(new Dictionary<string, string> { ["netflix.com"] = "9.9.9.9" }, Hosts);

        var run = AutoPickJournal.Load(Journal).Single();

        Assert.Equal(AutoPickJournal.ChangedLater, AutoPickJournal.RestoreAll(run, Rules, Journal, Hosts).Single());
        Assert.Contains(UserRulesFile.Load(Rules).Entries, e => e.Mode == RoutingMode.Direct && e.Matches("*.netflix.com"));
        Assert.Equal("9.9.9.9", HostsEditor.Pins(Hosts)["netflix.com"]);
    }

    [Fact]
    public void Pin_removed_by_hand_counts_as_returned()
    {
        var row = Row(RoutingMode.Direct, Try(AutoPickWay.Direct, false), PinTry(), PinTry());
        row.Choice = AutoPick.Choose(row);

        AutoPick.Apply(Report(row), Rules, Journal, _ => (RoutingMode.Direct, null, null), Hosts);
        HostsEditor.Unpin(["netflix.com", "www.netflix.com"], Hosts);

        var run = AutoPickJournal.Load(Journal).Single();

        Assert.Null(AutoPickJournal.RestoreAll(run, Rules, Journal, Hosts).Single());
        Assert.True(AutoPickJournal.Load(Journal).Single().Entries.Single().Restored);
    }

    [Fact]
    public void Name_already_in_hosts_is_not_overwritten()
    {
        File.AppendAllText(Hosts, "5.6.7.8 netflix.com\r\n");

        var row = Row(RoutingMode.Direct, Try(AutoPickWay.Direct, false), PinTry(), PinTry());
        row.Choice = AutoPick.Choose(row);

        AutoPick.Apply(Report(row), Rules, Journal, _ => (RoutingMode.Direct, null, null), Hosts);

        Assert.Contains("5.6.7.8 netflix.com", File.ReadAllText(Hosts));
        Assert.False(HostsEditor.Pins(Hosts).ContainsKey("netflix.com"));
    }
}
