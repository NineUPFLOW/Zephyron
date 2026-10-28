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

    public static bool Looks(string text) => text.TrimStart().StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>Файлы .conf из ключа и имя сервера; пусто — WireGuard внутри нет.</summary>
    public static (IReadOnlyList<string> Configs, string? Name) Decode(string text)
    {
        var body = text.Trim()[Scheme.Length..].Replace('-', '+').Replace('_', '/').TrimEnd('=');
        var bytes = Convert.FromBase64String(body.PadRight(body.Length + (4 - body.Length % 4) % 4, '='));

        string json;

        if (bytes.Length > 6 && bytes[4] == 0x78)
        {
            using var zlib = new ZLibStream(new MemoryStream(bytes, 4, bytes.Length - 4), CompressionMode.Decompress);
            using var reader = new StreamReader(zlib, Encoding.UTF8);
            json = reader.ReadToEnd();
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
