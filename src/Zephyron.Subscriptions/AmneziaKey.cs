using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Zephyron.Subscriptions;

/// <summary>
/// Ключ AmneziaVPN <c>vpn://…</c> — то, что отдаёт «Поделиться» в самой
/// AmneziaVPN, без выгрузки файла .conf.
/// </summary>
/// <remarks>
/// Внутри — base64url от сжатого Qt JSON (<c>qCompress</c>: четыре байта
/// длины и поток zlib; старые ключи — без сжатия). В JSON — контейнеры,
/// у WireGuard и AmneziaWG в <c>last_config</c> лежит готовый .conf.
/// Контейнеры Xray ссылкой не становятся: их ключ дают отдельно, vless://.
/// </remarks>
public static class AmneziaKey
{
    public const string Scheme = "vpn://";

    private const int MaxKey = 64 * 1024;
    private const int MaxJson = 1024 * 1024;

    public static bool Looks(string text) => text.TrimStart().StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>Файлы .conf из ключа и имя сервера; пусто — WireGuard внутри нет.</summary>
    public static (IReadOnlyList<string> Configs, string? Name) Decode(string text)
    {
        var body = text.Trim()[Scheme.Length..].Replace('-', '+').Replace('_', '/').TrimEnd('=');

        // Настоящий ключ — несколько килобайт; больше — не ключ, а повод упасть.
        if (body.Length > MaxKey)
            throw new InvalidDataException("ключ слишком длинный");

        var bytes = Convert.FromBase64String(body.PadRight(body.Length + (4 - body.Length % 4) % 4, '='));

        string json;

        if (bytes.Length > 6 && bytes[4] == 0x78)
        {
            // Сжатое разворачивается не дальше мегабайта: крошечный ключ мог бы раздуться в гигабайты.
            using var zlib = new ZLibStream(new MemoryStream(bytes, 4, bytes.Length - 4), CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[16384];

            for (int read; (read = zlib.Read(buffer)) > 0;)
            {
                if (output.Length + read > MaxJson)
                    throw new InvalidDataException("ключ разворачивается слишком большим");

                output.Write(buffer, 0, read);
            }

            json = Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
        }
        else
        {
            json = Encoding.UTF8.GetString(bytes);
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var name = root.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        var configs = new List<string>();

        if (root.TryGetProperty("containers", out var containers) && containers.ValueKind == JsonValueKind.Array)
        {
            foreach (var container in containers.EnumerateArray())
            {
                if (container.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var part in container.EnumerateObject())
                {
                    if (part.Value.ValueKind == JsonValueKind.Object
                        && part.Value.TryGetProperty("last_config", out var last)
                        && last.ValueKind == JsonValueKind.String
                        && Conf(last.GetString()) is { } conf)
                        configs.Add(conf);
                }
            }
        }

        return (configs, string.IsNullOrWhiteSpace(name) ? null : name);
    }

    private static string? Conf(string? lastConfig)
    {
        if (string.IsNullOrWhiteSpace(lastConfig))
            return null;

        try
        {
            using var inner = JsonDocument.Parse(lastConfig);
            return inner.RootElement.TryGetProperty("config", out var c) && c.GetString() is { } text && WireGuardConf.Looks(text)
                ? text
                : null;
        }
        catch (JsonException)
        {
            return WireGuardConf.Looks(lastConfig) ? lastConfig : null;
        }
    }
}
