namespace Zephyron.Subscriptions;

/// <summary>
/// Flow VLESS так, как его понимает sing-box.
/// </summary>
/// <remarks>
/// sing-box принимает только пустой flow и <c>xtls-rprx-vision</c>; прочее («xtls-rprx-direct»,
/// «none», заглавные) он отвергает фатально и роняет весь туннель, а не один сервер.
/// <c>xtls-rprx-vision-udp443</c> — клиентский вариант Vision из Xray (лишь не глушит UDP/443,
/// сервер ждёт обычный Vision), поэтому переводится в <c>xtls-rprx-vision</c>.
/// Остальное незнакомое — <c>null</c>: компилятор flow не пишет, сервер остаётся (проверено тестом Flow).
/// </remarks>
public static class VlessFlows
{
    public const string Vision = "xtls-rprx-vision";

    /// <summary>
    /// Flow для sing-box: пустая строка — без flow; <c>null</c> — sing-box такого не знает.
    /// </summary>
    public static string? ForSingBox(string? flow)
    {
        if (string.IsNullOrWhiteSpace(flow))
            return string.Empty;

        return flow.Trim().ToLowerInvariant() is Vision or "xtls-rprx-vision-udp443" ? Vision : null;
    }
}
