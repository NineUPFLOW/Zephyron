namespace Zephyron.Core.Rules;

/// <summary>
/// Правила «программа → …»: разовое снятие 04.10 и возврат 11.10.
/// </summary>
/// <remarks>
/// <para>
/// Окно обещало: «Через VPN» для программы заводит в туннель весь трафик
/// машины. С 01.10 это верно только без десинка
/// (<see cref="ProgramCapture.ForcesFullCapture"/>): в «Гибриде» правило
/// действует лишь на соединения, попавшие в туннель по другим правилам,
/// а серверы игры по голым адресам идут напрямую. Человек ставил игру
/// «через VPN» и не получал ничего — Dead by Daylight у Максима, 04.10:
/// «добавление программы ничем не помогло». Владелец: «убери их, а то мы
/// их обманываем».
/// </para>
/// <para>
/// Снятые запоминаются в настройках (<see cref="AppSettings.RemovedProgramRules"/>),
/// чтобы предложить их вернуть, когда обход по программе переделают.
/// Переделали 11.10: полный перехват закрыл двойной проход адресами TUN
/// и снова работает при десинке, поэтому правила возвращаются —
/// <see cref="Restore"/>, кнопкой в «Маршрутах».
/// </para>
/// </remarks>
public static class ProgramRulesOff
{
    /// <summary>
    /// Снимает правила по программе, если это ещё не делалось.
    /// </summary>
    /// <returns>Снятые правила словами: «Diablo IV.exe → VPN»; пусто — снимать было нечего или уже снято.</returns>
    public static IReadOnlyList<string> RemoveOnce(string settingsPath, string rulesPath)
    {
        var settings = AppSettings.Load(settingsPath);

        if (settings.ProgramRulesRemoved == true)
            return [];

        var file = UserRulesFile.Load(rulesPath);
        var programs = file.Entries.Where(e => e.Match == MatchKind.Process).ToList();

        if (programs.Count > 0)
        {
            file.ReplaceAll(file.Entries.Where(e => e.Match != MatchKind.Process).ToList());
            file.Save();
        }

        var removed = programs.Select(Describe).ToList();

        (settings with
        {
            ProgramRulesRemoved = true,
            RemovedProgramRules = removed.Count > 0 ? removed : settings.RemovedProgramRules,
        }).Save(settingsPath);

        return removed;
    }

    /// <summary>Правило словами: «Diablo IV.exe → VPN».</summary>
    public static string Describe(UserRuleEntry entry) => $"{entry.Value} → {entry.DescribeMode()}";

    /// <summary>
    /// Возвращает снятые правила по программе — «через VPN», как их помнят настройки.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Обход по программе возвращён 11.10: «через VPN» заводит программу
    /// в туннель и при десинке (<see cref="ProgramCapture.ForcesFullCapture"/>).
    /// Молча вернуть правила нельзя — человек их не просил, — поэтому это
    /// действие кнопки, а не перевода при запуске.
    /// </para>
    /// <para>
    /// Возвращаются «через VPN»: прежний режим не сохранялся, а именно его
    /// человек и выбирал, ставя программу в VPN. Запись, уже стоящую у имени,
    /// не трогаем: свои правки после снятия важнее.
    /// </para>
    /// </remarks>
    /// <returns>Сколько правил вернулось; ноль — возвращать было нечего.</returns>
    public static int Restore(string settingsPath, string rulesPath)
    {
        var settings = AppSettings.Load(settingsPath);

        if (settings.RemovedProgramRules is not { Count: > 0 } remembered)
            return 0;

        var file = UserRulesFile.Load(rulesPath);
        int restored = 0;

        foreach (var program in remembered)
        {
            var value = program.Split(" → ")[0].Trim();

            if (value.Length == 0 || file.Entries.Any(e => e.Match == MatchKind.Process
                && e.Value.Equals(value, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            file.Set(MatchKind.Process, value, RoutingMode.Proxy, recipe: null);
            restored++;
        }

        if (restored > 0)
            file.Save();

        (settings with { ProgramRulesRemoved = true, RemovedProgramRules = null }).Save(settingsPath);

        return restored;
    }
}
