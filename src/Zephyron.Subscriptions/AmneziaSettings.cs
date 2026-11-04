using System.Globalization;
using System.Text.Json.Nodes;

namespace Zephyron.Subscriptions;

/// <summary>
/// Маскировка AmneziaWG — из ссылки или файла <c>.conf</c> в блок <c>amnezia</c> sing-box.
/// </summary>
/// <remarks>
/// <para>
/// Типы проверены на нашем движке (1.14.1-extended-2.7.2, <c>sing-box check</c>,
/// 01.10): <c>h1</c>–<c>h4</c> он принимает и числом, и строкой-диапазоном
/// («100-200», так их пишет AmneziaWG 2.0), <c>i1</c>–<c>i5</c> — строками,
/// а <c>jc</c>, <c>jmin</c>, <c>jmax</c>, <c>s1</c>–<c>s4</c> — только числом:
/// <c>"jc": "4"</c> в кавычках роняет весь конфиг, а с ним и все остальные
/// серверы. Поэтому нечисловое значение здесь — ошибка разбора этого ключа,
/// а не строка в конфиге.
/// </para>
/// <para>
/// Ключи в ссылке и в файле те же, что у самой AmneziaWG (<c>Jc</c>,
/// <c>H1</c>, <c>I1</c>), регистр не важен.
/// </para>
/// </remarks>
public static class AmneziaSettings
{
    private static readonly string[] Numbers = ["jc", "jmin", "jmax", "s1", "s2", "s3", "s4"];
    private static readonly string[] Headers = ["h1", "h2", "h3", "h4"];
    private static readonly string[] Signatures = ["i1", "i2", "i3", "i4", "i5"];

    /// <param name="get">Значение по имени ключа без учёта регистра, <c>null</c> — нет такого.</param>
    /// <returns>Блок JSON строкой; <c>null</c>, если маскировки нет — обычный WireGuard.</returns>
    /// <exception cref="FormatException">Числовой ключ задан не числом.</exception>
    public static string? From(Func<string, string?> get)
    {
        var block = new JsonObject();

        foreach (var key in Numbers)
        {
            var text = get(key)?.Trim();

            if (string.IsNullOrEmpty(text))
                continue;

            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                throw new FormatException($"параметр AmneziaWG {key.ToUpperInvariant()} = «{text}» — не число");

            block[key] = number;
        }

        foreach (var key in Headers)
        {
            var text = get(key)?.Trim();

            if (string.IsNullOrEmpty(text))
                continue;

            // До 2^32: у AmneziaWG заголовки беззнаковые 32-битные, в int не влезают.
            block[key] = long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? number
                : text;
        }

        foreach (var key in Signatures)
        {
            var text = get(key)?.Trim();

            if (!string.IsNullOrEmpty(text))
                block[key] = text;
        }

        return block.Count == 0 ? null : block.ToJsonString();
    }

    /// <summary>
    /// Параметры маскировки, которых движок не знает: J1–J3 и Itime у AmneziaWG 1.5,
    /// параметры AmneziaWG 3.x и всё, что похоже на параметр маскировки, но не из наших списков.
    /// </summary>
    /// <remarks>
    /// Раньше такие ключи отбрасывались молча, и сервер с ними «не отвечал» без слова о причине.
    /// Движок знает только jc, jmin, jmax, s1–s4, h1–h4, i1–i5.
    /// </remarks>
    public static IReadOnlyList<string> Unsupported(IEnumerable<string> keys) =>
        keys.Where(k => (LooksAmnezia.IsMatch(k) || Generation3Name(k) is not null)
                && !Numbers.Concat(Headers).Concat(Signatures).Contains(k, StringComparer.OrdinalIgnoreCase))
            .Select(Display)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Оговорка к серверу по ключам, которых движок не знает; <c>null</c> — говорить не о чем.
    /// </summary>
    /// <remarks>
    /// HeaderProtectionKey шифрует заголовки и обязан совпасть на обеих сторонах, RandomTrailers
    /// во включённом виде — так же: без них сервер молча отбрасывает пакеты. Остальное
    /// у AmneziaWG 3 одностороннее и формат пакетов не меняет — о нём молчим.
    /// </remarks>
    public static string? Note(IReadOnlyDictionary<string, string> extra)
    {
        var parts = new List<string>();

        var breaking = extra
            .Where(p => Generation3Name(p.Key) is "HeaderProtectionKey" && !string.IsNullOrWhiteSpace(p.Value)
                || Generation3Name(p.Key) is "RandomTrailers" && IsOn(p.Value))
            .Select(p => Display(p.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (breaking.Count > 0)
            parts.Add($"это AmneziaWG 3 ({string.Join(", ", breaking)}), а движок говорит только на 2.0 — сервер не ответит");

        var older = Unsupported(extra.Keys).Where(k => Generation3Name(k) is null).ToList();

        if (older.Count > 0)
            parts.Add($"параметров AmneziaWG {string.Join(", ", older)} движок не знает — сервер может не ответить");

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private static bool IsOn(string? value) =>
        value?.Trim().ToLowerInvariant() is "on" or "true" or "1" or "yes";

    /// <summary>Каноническое имя параметра AmneziaWG 3.x; не из них — <c>null</c>. В файле CamelCase, в JSON — через «_».</summary>
    private static string? Generation3Name(string key) =>
        Generation3.FirstOrDefault(n => n.Equals(key.Replace("_", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase));

    private static string Display(string key) =>
        Generation3Name(key)
            ?? (key.Length <= 2 ? key.ToUpperInvariant() : char.ToUpperInvariant(key[0]) + key[1..].ToLowerInvariant());

    // PersistentKeepalive здесь нет: он у обычного WireGuard, в [Peer], и читается отдельно.
    private static readonly string[] Generation3 =
    [
        "HeaderProtectionKey", "ContentPaddingAddition", "RandomTrailers", "DisableCookies",
        "RekeyAfterTime", "RekeyTimeout", "RejectAfterTime", "KeepaliveTimeout", "MaxHandshakeAttempts",
    ];

    private static readonly System.Text.RegularExpressions.Regex LooksAmnezia = new(
        @"^(?:[jshi]\d+|jc|jmin|jmax|itime)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
