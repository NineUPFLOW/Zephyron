using Zephyron.Core.Rules;
using Zephyron.Supervisor;
using Zephyron.Zapret;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>
/// Матрица Choose, нормализация имён, откат Apply и устойчивость журнала автоподбора.
/// </summary>
public sealed class AutoPickMatrixTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "autopick-m-" + Guid.NewGuid().ToString("N"));

    public AutoPickMatrixTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Rules => Path.Combine(_dir, "rules.user.yaml");

    private string Journal => Path.Combine(_dir, "journal.json");

    private static AutoPickTry Try(AutoPickWay way, bool works, double seconds = 1, string? recipe = null) =>
        new(way, recipe, recipe, way == AutoPickWay.Vpn ? "srv" : null, works, TimeSpan.FromSeconds(seconds), "");

    private static AutoPickTry D(bool w) => Try(AutoPickWay.Direct, w);

    private static AutoPickTry P(bool w) => Try(AutoPickWay.Preset, w);

    private static AutoPickTry V(bool w) => Try(AutoPickWay.Vpn, w);

    private static AutoPickTry R(string name, bool w, double s = 1) => Try(AutoPickWay.Recipe, w, s, name);

    private static string Pick(RoutingMode mode, string? recipe, bool presetRan, params AutoPickTry[] tries)
    {
        var row = new AutoPickRow(new AutoPickTarget("a.example.com", "Пример"), mode, recipe) { PresetRan = presetRan };

        foreach (var t in tries)
            row.Add(t);

        var c = AutoPick.Choose(row);

        return c is null ? "keep" : $"{c.Way}/{c.Mode}/{c.Recipe}";
    }

    private static readonly string PresetPick = "Preset/Desync/" + DesyncRecipes.FromPreset;

    [Fact]
    public void Choose_matrix_current_direct()
    {
        Assert.Equal("keep", Pick(RoutingMode.Direct, null, true, D(true), P(true), V(true)));
        Assert.Equal(PresetPick, Pick(RoutingMode.Direct, null, true, D(false), P(true), V(true)));
        Assert.Equal("Recipe/Desync/r", Pick(RoutingMode.Direct, null, true, D(false), P(false), R("r", true), V(true)));
        Assert.Equal("Vpn/Proxy/", Pick(RoutingMode.Direct, null, true, D(false), P(false), R("r", false), V(true)));
        Assert.Equal("keep", Pick(RoutingMode.Direct, null, true, D(false), P(false), V(false)));
    }

    [Fact]
    public void Choose_direct_current_route_unmeasured_is_not_replaced() =>
        Assert.Equal("keep", Pick(RoutingMode.Direct, null, true, P(true), V(true)));

    [Fact]
    public void Choose_matrix_current_preset_desync()
    {
        Assert.Equal("keep", Pick(RoutingMode.Desync, null, true, D(false), P(true), V(true)));
        Assert.Equal("Direct/Direct/", Pick(RoutingMode.Desync, null, true, D(true), P(false)));
        Assert.Equal("Recipe/Desync/r", Pick(RoutingMode.Desync, null, true, D(false), P(false), R("r", true)));
        Assert.Equal("Vpn/Proxy/", Pick(RoutingMode.Desync, null, true, D(false), P(false), V(true)));

        // Пробы пресета нет, стадия прошла: путь равен прямому.
        Assert.Equal("keep", Pick(RoutingMode.Desync, null, true, D(true), V(true)));
        Assert.Equal("Vpn/Proxy/", Pick(RoutingMode.Desync, null, true, D(false), V(true)));

        // Стадия пресета не прошла: пробы нет - значит неизвестно.
        Assert.Equal("keep", Pick(RoutingMode.Desync, null, false, D(false), V(true)));
        Assert.Equal("keep", Pick(RoutingMode.Desync, null, false, D(true)));
    }

    [Fact]
    public void Choose_matrix_current_recipe_desync()
    {
        Assert.Equal("keep", Pick(RoutingMode.Desync, "X", true, D(true), R("X", true)));
        Assert.Equal("Direct/Direct/", Pick(RoutingMode.Desync, "X", true, D(true), R("X", false)));
        Assert.Equal(PresetPick, Pick(RoutingMode.Desync, "x", true, D(false), P(true), R("X", false)));
        Assert.Equal("Vpn/Proxy/", Pick(RoutingMode.Desync, "X", true, D(false), R("X", false), V(true)));
        Assert.Equal("keep", Pick(RoutingMode.Desync, "X", true, D(false), V(true)));
        Assert.Equal("keep", Pick(RoutingMode.Desync, "x", true, R("X", true), D(false)));
        Assert.Equal("keep", Pick(RoutingMode.Desync, "X", true, D(false), R("Y", true)));
    }

    [Fact]
    public void Choose_matrix_current_vpn()
    {
        Assert.Equal("keep", Pick(RoutingMode.Proxy, null, true, D(true), V(true)));
        Assert.Equal("Direct/Direct/", Pick(RoutingMode.Proxy, null, true, D(true), V(false)));
        Assert.Equal(PresetPick, Pick(RoutingMode.Proxy, null, true, D(false), P(true), V(false)));
        Assert.Equal("Recipe/Desync/fast", Pick(RoutingMode.Proxy, null, true, D(false), R("slow", true, 3), R("fast", true, 1), V(false)));
        Assert.Equal("keep", Pick(RoutingMode.Proxy, null, true, D(true)));
        Assert.Equal("keep", Pick(RoutingMode.Proxy, null, true, D(false), P(false), V(false)));
        Assert.Equal("keep", Pick(RoutingMode.Proxy, null, true));
    }

    private static RecipeTrial.Flow Flow(int? status, bool cert) =>
        new() { Bytes = 10, Ending = "", Status = status, CertValid = cert };

    [Fact]
    public void Works_flow_status_table()
    {
        Assert.True(AutoPick.Works(Flow(302, true)));
        Assert.False(AutoPick.Works(Flow(302, false)));
        Assert.False(AutoPick.Works(Flow(403, false)));
        Assert.True(AutoPick.Works(Flow(403, true)));
        Assert.False(AutoPick.Works(Flow(451, false)));
        Assert.False(AutoPick.Works(Flow(451, true)));
        Assert.False(AutoPick.Works(Flow(null, true)));
    }

    [Fact]
    public void Normalize_collapses_wildcard_and_case_but_not_www()
    {
        Assert.Equal("x.com", AutoPickRow.Normalize("*.x.com"));
        Assert.Equal("x.com", AutoPickRow.Normalize("X.com"));
        Assert.Equal("x.com", AutoPickRow.Normalize("  .x.com "));
        Assert.Equal("www.x.com", AutoPickRow.Normalize("www.x.com"));
        Assert.Equal("x.com", new AutoPickRow(new AutoPickTarget("*.X.com", "l"), RoutingMode.Direct, null).Host);
    }

    private static AutoPickJournalRun Run(params AutoPickJournalEntry[] entries) => new()
    {
        At = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero),
        Preset = "p",
        Entries = [.. entries],
    };

    private AutoPickJournalEntry Write(string host)
    {
        var file = UserRulesFile.Load(Rules);
        var value = "*." + host;
        file.Set(MatchKind.Domain, value, RoutingMode.Direct);
        file.Save();

        return new AutoPickJournalEntry
        {
            Host = host,
            Value = value,
            Wrote = UserRulesFile.Load(Rules).Entries.First(e => e.Matches(value)),
        };
    }

    private static AutoPickReport Report(params string[] hosts)
    {
        var rows = hosts.Select(h =>
        {
            var row = new AutoPickRow(new AutoPickTarget(h, h), RoutingMode.Proxy, null);
            row.Add(Try(AutoPickWay.Direct, true));
            row.Add(Try(AutoPickWay.Vpn, false));
            row.Choice = AutoPick.Choose(row);

            return row;
        }).ToList();

        return new AutoPickReport("p", rows, new Dictionary<string, TimeSpan>(), []);
    }

    [Fact]
    public void Apply_throwing_evaluate_rolls_file_back_and_leaves_journal()
    {
        var seed = UserRulesFile.Load(Rules);
        seed.ReplaceAll([new UserRuleEntry { Match = MatchKind.Domain, Value = "*.example.com", Mode = RoutingMode.Proxy }]);
        seed.Save();
        AutoPickJournal.Append(Run(), Journal);

        var rulesBefore = File.ReadAllBytes(Rules);
        var journalBefore = File.ReadAllBytes(Journal);

        Assert.Throws<InvalidOperationException>(() =>
            AutoPick.Apply(Report("a.example.com"), Rules, Journal, _ => throw new InvalidOperationException("boom")));

        Assert.Equal(rulesBefore, File.ReadAllBytes(Rules));
        Assert.Equal(journalBefore, File.ReadAllBytes(Journal));
    }

    [Fact]
    public void Apply_throwing_evaluate_without_rules_file_leaves_no_file_and_no_journal()
    {
        Assert.Throws<InvalidOperationException>(() =>
            AutoPick.Apply(Report("a.example.com"), Rules, Journal, _ => throw new InvalidOperationException("boom")));

        Assert.False(File.Exists(Rules));
        Assert.False(File.Exists(Journal));
    }

    [Fact]
    public void Apply_with_no_choices_touches_nothing()
    {
        var row = new AutoPickRow(new AutoPickTarget("a.com", "a"), RoutingMode.Direct, null);
        var report = new AutoPickReport("p", [row], new Dictionary<string, TimeSpan>(), []);

        Assert.Empty(AutoPick.Apply(report, Rules, Journal, _ => (RoutingMode.Direct, null, null)));
        Assert.False(File.Exists(Rules));
        Assert.False(File.Exists(Journal));
    }

    [Fact]
    public void Apply_two_hosts_are_journaled_and_fully_restorable()
    {
        var outcome = AutoPick.Apply(Report("a.com", "b.com"), Rules, Journal, _ => (RoutingMode.Direct, null, null));

        Assert.All(outcome, o => Assert.True(o.Written));
        Assert.Equal(2, UserRulesFile.Load(Rules).Entries.Count);

        var run = AutoPickJournal.Load(Journal).Single();

        Assert.All(AutoPickJournal.RestoreAll(run, Rules, Journal), r => Assert.Null(r));
        Assert.Empty(UserRulesFile.Load(Rules).Entries);
    }

    [Fact]
    public void Restore_entry_with_missing_wrote_gives_reason()
    {
        var entry = Write("a.com");
        entry.Wrote = null;
        var run = Run(entry);
        AutoPickJournal.Append(run, Journal);

        Assert.Equal("запись журнала повреждена", AutoPickJournal.Restore(run, entry, Rules, Journal));
        Assert.Single(UserRulesFile.Load(Rules).Entries);
    }

    [Fact]
    public void Restore_entry_removed_by_user_is_changed_later()
    {
        var entry = Write("a.com");
        var run = Run(entry);
        AutoPickJournal.Append(run, Journal);

        var file = UserRulesFile.Load(Rules);
        file.RemoveAt(0);
        file.Save();

        Assert.Equal(AutoPickJournal.ChangedLater, AutoPickJournal.Restore(run, entry, Rules, Journal));
    }

    [Fact]
    public void RestoreAll_twice_is_idempotent()
    {
        var entry = Write("a.com");
        var run = Run(entry);
        AutoPickJournal.Append(run, Journal);

        Assert.Null(AutoPickJournal.RestoreAll(run, Rules, Journal)[0]);

        // Вторая попытка с копией прогона, прочитанной с диска.
        var again = AutoPickJournal.RestoreAll(AutoPickJournal.Load(Journal)[0], Rules, Journal);

        Assert.Equal("уже возвращено", again[0]);
        Assert.Empty(UserRulesFile.Load(Rules).Entries);
        Assert.True(AutoPickJournal.Load(Journal)[0].Entries[0].Restored);
    }

    [Fact]
    public void RestoreAll_stale_copy_does_not_touch_rule_added_after_restore()
    {
        var entry = Write("a.com");
        var run = Run(entry);
        AutoPickJournal.Append(run, Journal);
        AutoPickJournal.RestoreAll(run, Rules, Journal);

        var file = UserRulesFile.Load(Rules);
        file.Set(MatchKind.Domain, entry.Value, RoutingMode.Proxy);
        file.Save();

        Assert.Equal("уже возвращено", AutoPickJournal.RestoreAll(run, Rules, Journal)[0]);
        Assert.Single(UserRulesFile.Load(Rules).Entries);
    }

    [Fact]
    public void Corrupted_journal_load_does_not_throw_and_append_keeps_old_text()
    {
        File.WriteAllText(Journal, "{ not json [[");

        Assert.Empty(AutoPickJournal.Load(Journal));
        Assert.Equal("{ not json [[", File.ReadAllText(Journal + ".broken"));

        File.WriteAllText(Journal, "garbage");
        AutoPickJournal.Append(Run(), Journal);

        Assert.Single(AutoPickJournal.Load(Journal));
        Assert.Equal("garbage", File.ReadAllText(Journal + ".broken"));
    }

    [Fact]
    public void Append_does_not_overwrite_locked_journal()
    {
        AutoPickJournal.Append(Run(), Journal);
        var before = File.ReadAllBytes(Journal);

        using (new FileStream(Journal, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Empty(AutoPickJournal.Load(Journal));
            Assert.ThrowsAny<IOException>(() => AutoPickJournal.Append(Run(), Journal));
        }

        Assert.Equal(before, File.ReadAllBytes(Journal));
    }
}
