using Zephyron.Core.Rules;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>
/// Правила по программе: разовое снятие 04.10 и возврат 11.10 (владелец).
/// </summary>
public sealed class ProgramRulesOffTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("zy-programs-").FullName;

    private string SettingsPath => Path.Combine(_dir, "zephyron.json");

    private string RulesPath => Path.Combine(_dir, "rules.user.yaml");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void WriteRules(params (MatchKind Match, string Value, RoutingMode Mode)[] rules)
    {
        var file = UserRulesFile.Load(RulesPath);

        foreach (var (match, value, mode) in rules)
            file.Set(match, value, mode);

        file.Save();
    }

    [Fact]
    public void Program_rules_are_removed_and_remembered()
    {
        AppSettings.Fresh.Save(SettingsPath);
        WriteRules(
            (MatchKind.Process, "Diablo IV.exe", RoutingMode.Proxy),
            (MatchKind.Domain, "*.example.com", RoutingMode.Direct));

        var removed = ProgramRulesOff.RemoveOnce(SettingsPath, RulesPath);

        Assert.Equal(["Diablo IV.exe → VPN"], removed);

        var left = UserRulesFile.Load(RulesPath).Entries;
        Assert.DoesNotContain(left, e => e.Match == MatchKind.Process);
        Assert.Contains(left, e => e.Match == MatchKind.Domain);

        var settings = AppSettings.Load(SettingsPath);
        Assert.True(settings.ProgramRulesRemoved);
        Assert.Equal(["Diablo IV.exe → VPN"], settings.RemovedProgramRules);
    }

    /// <summary>Один раз: вписанное руками после перевода остаётся, и запомненное не затирается.</summary>
    [Fact]
    public void Removal_happens_once()
    {
        AppSettings.Fresh.Save(SettingsPath);
        WriteRules((MatchKind.Process, "Diablo IV.exe", RoutingMode.Proxy));
        ProgramRulesOff.RemoveOnce(SettingsPath, RulesPath);

        WriteRules((MatchKind.Process, "game.exe", RoutingMode.Direct));

        Assert.Empty(ProgramRulesOff.RemoveOnce(SettingsPath, RulesPath));
        Assert.Contains(UserRulesFile.Load(RulesPath).Entries, e => e.Match == MatchKind.Process);
        Assert.Equal(["Diablo IV.exe → VPN"], AppSettings.Load(SettingsPath).RemovedProgramRules);
    }

    /// <summary>Возврат: снятое правило встаёт «через VPN», запись человека не трогается.</summary>
    [Fact]
    public void Restore_returns_removed_programs_as_vpn()
    {
        AppSettings.Fresh.Save(SettingsPath);
        WriteRules(
            (MatchKind.Process, "Diablo IV.exe", RoutingMode.Proxy),
            (MatchKind.Domain, "*.example.com", RoutingMode.Direct));
        ProgramRulesOff.RemoveOnce(SettingsPath, RulesPath);

        // После снятия человек сам вписал программу «напрямую» — её не трогаем.
        WriteRules((MatchKind.Process, "Diablo IV.exe", RoutingMode.Direct));

        var restored = ProgramRulesOff.Restore(SettingsPath, RulesPath);

        Assert.Equal(0, restored);

        var entries = UserRulesFile.Load(RulesPath).Entries;
        var diablo = Assert.Single(entries, e => e.Match == MatchKind.Process && e.Value == "Diablo IV.exe");
        Assert.Equal(RoutingMode.Direct, diablo.Mode);
        Assert.Null(AppSettings.Load(SettingsPath).RemovedProgramRules);
    }

    /// <summary>Возврат чистого случая: имени нет — встаёт «через VPN».</summary>
    [Fact]
    public void Restore_returns_the_vpn_route_when_the_name_is_free()
    {
        AppSettings.Fresh.Save(SettingsPath);
        WriteRules((MatchKind.Process, "Diablo IV.exe", RoutingMode.Proxy));
        ProgramRulesOff.RemoveOnce(SettingsPath, RulesPath);

        var restored = ProgramRulesOff.Restore(SettingsPath, RulesPath);

        Assert.Equal(1, restored);

        var entry = Assert.Single(UserRulesFile.Load(RulesPath).Entries, e => e.Match == MatchKind.Process);
        Assert.Equal("Diablo IV.exe", entry.Value);
        Assert.Equal(RoutingMode.Proxy, entry.Mode);
        Assert.Null(AppSettings.Load(SettingsPath).RemovedProgramRules);

        // Повторный возврат — уже нечего.
        Assert.Equal(0, ProgramRulesOff.Restore(SettingsPath, RulesPath));
    }
}
