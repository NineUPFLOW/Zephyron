namespace Zephyron.Core.Rules;

/// <summary>
/// Особый маршрут: на чём держится путь, помимо обычного режима.
/// </summary>
/// <remarks>
/// <para>
/// Пин живёт в hosts, маршрут — в <c>rules.user.yaml</c>, и по файлу маршрутов не понять,
/// что сервис держится на пине и на каком; чужой набор маршрутов его терял. Пометка
/// в записи правила (поля <c>special</c>, <c>pin</c>, <c>pin_hosts</c>) это и говорит.
/// Режим (<c>mode</c>) остаётся обычным и работает как прежде: пин сервиса — десинком,
/// пин посредника — напрямую, TG Proxy — десинком. Пины по-прежнему стоят в hosts:
/// файл лишь дописывает их туда при загрузке набора.
/// </para>
/// <para>Программа, не знающая поля, его пропустит; незнакомое слово — обычный маршрут.</para>
/// </remarks>
public static class SpecialRoute
{
    /// <summary>Имя прибито к адресу самого сервиса, трафик — под десинком.</summary>
    public const string ServicePin = "service-pin";

    /// <summary>Имя прибито к адресу посредника (DNS-посредник с зарубежным выходом), трафик — напрямую.</summary>
    public const string RelayPin = "relay-pin";

    /// <summary>Путь через прокси Telegram Desktop.</summary>
    public const string TgProxy = "tg-proxy";

    public static string? Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        ServicePin => ServicePin,
        RelayPin => RelayPin,
        TgProxy => TgProxy,
        _ => null,
    };

    /// <summary>Словами — для «Маршрутов» и отчёта.</summary>
    public static string Describe(string? special, string? pin = null) => special switch
    {
        ServicePin => "пин сервиса" + (pin is null ? "" : $" ({pin})"),
        RelayPin => "пин посредника" + (pin is null ? "" : $" {pin}"),
        TgProxy => "прокси Telegram",
        _ => "",
    };
}
