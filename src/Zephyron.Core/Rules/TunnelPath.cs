namespace Zephyron.Core.Rules;

/// <summary>Через что идёт туннель: подписка или WARP (владелец, 07.11).</summary>
public enum TunnelPath
{
    /// <summary>Подписки и ключи с вкладки VPN.</summary>
    Vpn,

    /// <summary>Встроенный или свой файл WARP.</summary>
    Warp,
}

/// <summary>
/// Выбор пути туннеля поверх прежнего поля <see cref="AppSettings.WarpEnabled"/> — хранение не менялось.
/// </summary>
/// <remarks>
/// Режимы (<see cref="WorkModes"/>) решают, поднят ли туннель и что в него уходит; путь — через что он идёт.
/// «Нет» нет: без туннеля — режим «Десинк». Маршрут «через VPN» идёт через выбранный путь.
/// </remarks>
public static class TunnelPaths
{
    public static string Name(TunnelPath path) => path == TunnelPath.Warp ? "WARP" : "VPN";

    public static TunnelPath Of(AppSettings settings) => settings.WarpEnabled ? TunnelPath.Warp : TunnelPath.Vpn;

    /// <summary>VPN без подписок и ключей выбрать нельзя — выбирать нечего; WARP — всегда.</summary>
    public static bool CanChoose(AppSettings settings, TunnelPath path) =>
        path != TunnelPath.Vpn || settings.HasSubscriptionExit;

    /// <summary>Настройки с выбранным путём; недоступный путь не применяется — настройки возвращаются как есть.</summary>
    public static AppSettings Apply(AppSettings settings, TunnelPath path) =>
        CanChoose(settings, path) ? settings with { WarpEnabled = path == TunnelPath.Warp } : settings;
}
