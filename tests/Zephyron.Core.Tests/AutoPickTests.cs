using Zephyron.Core.Rules;
using Zephyron.Supervisor;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>
/// Выбор способа, правило «работает» и откат журнала автоподбора.
/// </summary>
public sealed class AutoPickTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "autopick-" + Guid.NewGuid().ToString("N"));

    public AutoPickTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Rules => Path.Combine(_dir, "rules.user.yaml");

    private string Journal => Path.Combine(_dir, "journal.json");

    private static AutoPickTry Try(AutoPickWay way, bool works, double seconds = 1, string? recipe = null) =>
        new(way, recipe, recipe, way == AutoPickWay.Vpn ? "srv" : null, works, TimeSpan.FromSeconds(seconds), "");

    private static AutoPickRow Row(RoutingMode mode, string? recipe, params AutoPickTry[] tries)
    {
        var row = new AutoPickRow(new AutoPickTarget("a.example.com", "Пример"), mode, recipe);

        foreach (var attempt in tries)
            row.Add(attempt);

        return row;
    }

    [Fact]
    public void Working_current_route_stays()
    {
        var row = Row(RoutingMode.Proxy, null,
            Try(AutoPickWay.Direct, true), Try(AutoPickWay.Vpn, true));

        Assert.Null(AutoPick.Choose(row));
    }

    [Fact]
    public void Direct_beats_preset_beats_fastest_recipe_beats_vpn()
    {
        var all = new[]
        {
            Try(AutoPickWay.Direct, true),
            Try(AutoPickWay.Preset, true),
            Try(AutoPickWay.Recipe, true, 2, "slow"),
            Try(AutoPickWay.Recipe, true, 0.5, "fast"),
            Try(AutoPickWay.Vpn, true),
            Try(AutoPickWay.Recipe, false, 1, "gone"),
        };

        Assert.Equal(RoutingMode.Direct, AutoPick.Choose(Row(RoutingMode.Desync, "gone", all))!.Mode);

        var noDirect = all.Skip(1).ToArray();
        var preset = AutoPick.Choose(Row(RoutingMode.Desync, "gone", noDirect))!;
        Assert.Equal(RoutingMode.Desync, preset.Mode);
        Assert.Equal("", preset.Recipe);

        var recipe = AutoPick.Choose(Row(RoutingMode.Desync, "gone", noDirect.Skip(1).ToArray()))!;
        Assert.Equal("fast", recipe.Recipe);

        var vpn = AutoPick.Choose(Row(RoutingMode.Direct, null,
            Try(AutoPickWay.Direct, false), Try(AutoPickWay.Vpn, true)))!;
        Assert.Equal(RoutingMode.Proxy, vpn.Mode);
    }

    [Fact]
    public void Nothing_working_changes_nothing()
    {
        var row = Row(RoutingMode.Desync, null,
            Try(AutoPickWay.Direct, false), Try(AutoPickWay.Preset, false), Try(AutoPickWay.Vpn, false));

        Assert.Null(AutoPick.Choose(row));
    }

    [Fact]
    public void Skipped_row_changes_nothing()
    {
        var row = Row(RoutingMode.Proxy, null, Try(AutoPickWay.Direct, true), Try(AutoPickWay.Vpn, false));
        row.Skip = "античит";

        Assert.Null(AutoPick.Choose(row));
    }

    [Fact]
    public void Current_recipe_is_matched_by_its_own_try()
    {
        // Рецепт X работает - остаёмся, хотя напрямую тоже работало бы не здесь.
        var same = Row(RoutingMode.Desync, "X",
            Try(AutoPickWay.Direct, false), Try(AutoPickWay.Recipe, true, 1, "X"));

        Assert.Null(AutoPick.Choose(same));

        // Рецепт X сломан, соседний Y работает - переходим на Y.
        var other = Row(RoutingMode.Desync, "X",
            Try(AutoPickWay.Recipe, false, 1, "X"), Try(AutoPickWay.Recipe, true, 1, "Y"));

        Assert.Equal("Y", AutoPick.Choose(other)!.Recipe);
    }

    [Fact]
    public void Preset_route_without_section_counts_as_direct()
    {
        var row = Row(RoutingMode.Desync, null, Try(AutoPickWay.Direct, true));
        row.PresetRan = true;

        Assert.Null(AutoPick.Choose(row));

        // Direct не работает, а пресет не гонялся (стадия не прошла) - прямая
        // проба ничего не говорит о десинке: менять нечего.
        var unmeasured = Row(RoutingMode.Desync, null, Try(AutoPickWay.Direct, false), Try(AutoPickWay.Vpn, true));

        Assert.Null(AutoPick.Choose(unmeasured));
    }

    [Fact]
    public void Unmeasured_current_route_is_not_replaced()
    {
        // Рецепт X не мерили (стадия пропущена), но напрямую открывается.
        Assert.Null(AutoPick.Choose(Row(RoutingMode.Desync, "X", Try(AutoPickWay.Direct, true))));

        // Идёт через VPN, VPN не мерили.
        Assert.Null(AutoPick.Choose(Row(RoutingMode.Proxy, null, Try(AutoPickWay.Direct, true))));

        // Пресет мерили, он сломан, напрямую открывается - меняем.
        var broken = Row(RoutingMode.Desync, null, Try(AutoPickWay.Preset, false), Try(AutoPickWay.Direct, true));

        Assert.Equal(RoutingMode.Direct, AutoPick.Choose(broken)!.Mode);
    }

    private static RecipeTrial.Flow Flow(int status, bool cert) =>
        new() { Bytes = 10, Ending = "", Status = status, CertValid = cert };

    [Fact]
    public void Flow_works_rule_is_strict_about_stubs()
    {
        Assert.True(AutoPick.Works(Flow(200, false)));
        Assert.True(AutoPick.Works(Flow(403, true)));
        Assert.False(AutoPick.Works(Flow(403, false)));
        Assert.True(AutoPick.Works(Flow(302, true)));
        Assert.False(AutoPick.Works(Flow(302, false)));
        Assert.False(AutoPick.Works(Flow(451, true)));
        Assert.False(AutoPick.Works(Flow(503, true)));
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(301, true)]
    [InlineData(403, true)]
    [InlineData(404, true)]
    [InlineData(451, false)]
    [InlineData(500, false)]
    [InlineData(503, false)]
    [InlineData(null, false)]
    public void Works_rule(int? status, bool expected) => Assert.Equal(expected, AutoPick.Works(status));

    private static AutoPickJournalRun Run(params AutoPickJournalEntry[] entries) => new()
    {
        At = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero),
        Preset = "p",
        Entries = [.. entries],
    };

    private AutoPickJournalEntry Write(string host, UserRuleEntry? before)
    {
        var file = UserRulesFile.Load(Rules);
        var value = "*." + host;
        file.Set(MatchKind.Domain, value, RoutingMode.Direct);
        file.Save();

        return new AutoPickJournalEntry
        {
            Host = host,
            Value = value,
            Before = before,
            Wrote = UserRulesFile.Load(Rules).Entries.First(e => e.Matches(value)),
        };
    }

    [Fact]
    public void Restore_removes_inserted_entry()
    {
        var entry = Write("a.com", null);
        var run = Run(entry);
        AutoPickJournal.Append(run, Journal);

        Assert.Null(AutoPickJournal.Restore(run, entry, Rules, Journal));

        Assert.Empty(UserRulesFile.Load(Rules).Entries);
        Assert.True(AutoPickJournal.Load(Journal)[0].Entries[0].Restored);
    }

    [Fact]
    public void Restore_puts_replaced_entry_back_in_place()
    {
        var first = new UserRuleEntry { Match = MatchKind.Domain, Value = "*.first.com", Mode = RoutingMode.Proxy };
        var old = new UserRuleEntry { Match = MatchKind.Domain, Value = "*.a.com", Mode = RoutingMode.Desync, Recipe = "X" };
        var last = new UserRuleEntry { Match = MatchKind.Domain, Value = "*.last.com", Mode = RoutingMode.Proxy };

        var seed = UserRulesFile.Load(Rules);
        seed.ReplaceAll([first, old, last]);
        seed.Save();

        var entry = Write("a.com", old);
        var run = Run(entry);
        AutoPickJournal.Append(run, Journal);

        Assert.Null(AutoPickJournal.Restore(run, entry, Rules, Journal));

        var back = UserRulesFile.Load(Rules).Entries;
        Assert.Equal(["*.first.com", "*.a.com", "*.last.com"], back.Select(e => e.Value));
        Assert.Equal(old, back[1]);
    }

    [Fact]
    public void Entry_edited_afterwards_is_left_alone()
    {
        var entry = Write("a.com", null);
        var run = Run(entry);
        AutoPickJournal.Append(run, Journal);

        var file = UserRulesFile.Load(Rules);
        file.Set(MatchKind.Domain, entry.Value, RoutingMode.Proxy);
        file.Save();

        var results = AutoPickJournal.RestoreAll(run, Rules, Journal);

        Assert.Equal(AutoPickJournal.ChangedLater, results[0]);
        Assert.Equal(RoutingMode.Proxy, UserRulesFile.Load(Rules).Entries[0].Mode);
        Assert.False(AutoPickJournal.Load(Journal)[0].Entries[0].Restored);
    }

    [Fact]
    public void Apply_writes_before_wider_rule_backs_up_and_journals()
    {
        var wide = new UserRuleEntry { Match = MatchKind.Domain, Value = "*.example.com", Mode = RoutingMode.Proxy };
        var seed = UserRulesFile.Load(Rules);
        seed.ReplaceAll([wide]);
        seed.Save();

        var row = Row(RoutingMode.Proxy, null, Try(AutoPickWay.Direct, true), Try(AutoPickWay.Vpn, false));
        row.Choice = AutoPick.Choose(row);

        var report = new AutoPickReport("p", [row], new Dictionary<string, TimeSpan>(), []);

        var outcome = AutoPick.Apply(report, Rules, Journal, host => (RoutingMode.Direct, null, null));

        Assert.True(outcome.Single().Written);
        Assert.True(File.Exists(Path.Combine(_dir, "rules.user.before-autopick.yaml")));
        Assert.Equal(["*.a.example.com", "*.example.com"], UserRulesFile.Load(Rules).Entries.Select(e => e.Value));
        Assert.Single(AutoPickJournal.Load(Journal).Single().Entries);
    }

    [Fact]
    public void Apply_reverts_entry_that_another_rule_overrides()
    {
        var row = Row(RoutingMode.Proxy, null, Try(AutoPickWay.Direct, true), Try(AutoPickWay.Vpn, false));
        row.Choice = AutoPick.Choose(row);

        var report = new AutoPickReport("p", [row], new Dictionary<string, TimeSpan>(), []);

        var outcome = AutoPick.Apply(report, Rules, Journal, host => (RoutingMode.Proxy, null, "#1 чужое"));

        Assert.False(outcome.Single().Written);
        Assert.Contains("перекрыто правилом", outcome.Single().Note);
        Assert.Empty(UserRulesFile.Load(Rules).Entries);
        Assert.Empty(AutoPickJournal.Load(Journal));
    }
}
