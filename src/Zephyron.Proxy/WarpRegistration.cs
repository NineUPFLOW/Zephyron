namespace Zephyron.Proxy;

/// <summary>
/// Куда идёт регистрация устройства WARP, когда посредника из подписки нет.
/// </summary>
/// <remarks>
/// Движок регистрирует устройство на <see cref="Host"/> сам. У части провайдеров
/// адреса, которые выдаёт DNS (104.16.x), не принимают даже TCP, а другие узлы
/// Cloudflare на то же имя отвечают. Замер 07.11 у владельца: штатные 104.16.192.82
/// и 104.16.24.84 молчат; через эти узлы, с поднятым десинком, регистрация прошла
/// и WARP без подписки поднялся (warp=on, ChatGPT 200). Без десинка не отвечает ни один.
/// Адреса движок берёт по очереди (SingBoxOptions.WarpRegistrationEdges).
/// </remarks>
public static class WarpRegistration
{
    public const string Host = "api.cloudflareclient.com";

    public static readonly IReadOnlyList<string> Edges = ["104.18.10.10", "104.18.11.10", "104.17.0.1"];
}
