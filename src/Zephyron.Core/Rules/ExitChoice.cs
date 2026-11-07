namespace Zephyron.Core.Rules;

/// <summary>Выход за границу — то, что человек выбирает на «Главной» вместо режимов.</summary>
public enum ExitChoice
{
    /// <summary>Выход не выбран: работает один десинк.</summary>
    None,

    /// <summary>Встроенный или свой файл WARP.</summary>
    Warp,

    /// <summary>Подписки и ключи с вкладки VPN.</summary>
    Vpn,
}

/// <summary>
/// Выход и «весь трафик через выход» — над прежними полями настроек (владелец, 07.11).
/// </summary>
/// <remarks>
/// Хранение не менялось: выбор читается из <see cref="AppSettings.WarpEnabled"/>,
/// выключателей движков и наличия подписок, а <see cref="Apply"/> выставляет их
/// согласованно. Прежние режимы читаются так: «Десинк» — нет; «Гибрид» и «Туннель»
/// с WARP — WARP, с подпиской — Мой VPN; «Туннель» — «весь трафик» включён.
/// </remarks>
public static class Exits
{
    public static IReadOnlyList<ExitChoice> All { get; } = [ExitChoice.None, ExitChoice.Warp, ExitChoice.Vpn];

    public static string Name(ExitChoice exit) => exit switch
    {
        ExitChoice.Warp => "WARP",
        ExitChoice.Vpn => "Мой VPN",
        _ => "Нет",
    };

    /// <summary>Что выбор делает с трафиком — одной фразой.</summary>
    public static string Explain(ExitChoice exit) => exit switch
    {
        ExitChoice.Warp => "Бесплатный выход Cloudflare.",
        ExitChoice.Vpn => "Ваши подписки и ключи.",
        _ => "Только десинк, адрес домашний.",
    };

    /// <summary>Какой выход выбран; туннель без выхода — «Нет».</summary>
    public static ExitChoice Of(AppSettings settings)
    {
        if (!settings.Engines.Tunnel)
            return ExitChoice.None;

        if (settings.WarpEnabled)
            return ExitChoice.Warp;

        return settings.HasSubscriptionExit ? ExitChoice.Vpn : ExitChoice.None;
    }

    /// <summary>Идёт ли через выход весь трафик, а не только имена с маршрутом «через выход».</summary>
    public static bool AllTraffic(AppSettings settings) =>
        Of(settings) != ExitChoice.None && settings.Engines.TunnelTakesAll;

    /// <summary>«Мой VPN» без подписок и ключей выбрать нельзя — выбирать нечего.</summary>
    public static bool CanChoose(AppSettings settings, ExitChoice exit) =>
        exit != ExitChoice.Vpn || settings.HasSubscriptionExit;

    /// <summary>Включено ли «без исключений» (окно параметров туннеля) при выбранном выходе.</summary>
    public static bool IgnoresExclusions(AppSettings settings) =>
        Of(settings) != ExitChoice.None && settings.Engines.IgnoreExclusions;

    /// <summary>
    /// Выставляет WarpEnabled, выключатели движков и режим согласованно.
    /// </summary>
    /// <param name="allTraffic">Весь трафик через выход; <c>null</c> — как сейчас.</param>
    /// <remarks>
    /// «Мой VPN» без подписок не применяется: настройки возвращаются как есть.
    /// «Без исключений» живёт при «весь трафик» и сбрасывается, когда его снимают.
    /// </remarks>
    public static AppSettings Apply(AppSettings settings, ExitChoice exit, bool? allTraffic = null)
    {
        if (!CanChoose(settings, exit))
            return settings;

        var engines = settings.Engines;
        bool all = exit != ExitChoice.None && (allTraffic ?? AllTraffic(settings));

        var next = exit == ExitChoice.None
            ? engines with { Desync = true, Tunnel = false }
            : engines with { Desync = !all, Tunnel = true, IgnoreExclusions = all && engines.IgnoreExclusions };

        return (settings with { WarpEnabled = exit == ExitChoice.Warp }).With(next);
    }
}
