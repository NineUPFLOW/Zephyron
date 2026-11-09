using System.Text;

namespace Zephyron.Core;

/// <summary>
/// Описание сервера, приклеенное панелью к его имени: <c>имя?serverDescription=base64</c>.
/// </summary>
/// <remarks>
/// Так делает Remnawave для «своих» клиентов (Happ, FlClash X, INCY); получаем то же, когда
/// панель отвечает под именем Happ. Ссылки разбирает <c>ProxyUriParser</c>, а этот разбор
/// закрывает остальное: имена в документе Xray и теги, записанные в настройки прежними именами.
/// </remarks>
public static class ServerDescription
{
    private const string Marker = "?serverDescription=";

    /// <summary>Имя без хвоста и описание словами; хвоста нет — имя как есть и <c>null</c>.</summary>
    public static (string Name, string? Description) Split(string remark)
    {
        int at = remark.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return (remark, null);

        var name = remark[..at].Trim();
        var raw = remark[(at + Marker.Length)..].Trim();

        // Имя из одного хвоста — не имя: лучше оставить как пришло.
        if (name.Length == 0)
            return (remark, null);

        return (name, Decode(raw));
    }

    /// <summary>Имя без хвоста описания.</summary>
    /// <remarks>
    /// Теги серверов в настройках записаны прежними именами — с хвостом.
    /// Снимать его при чтении, иначе закреплённый сервер и исключения
    /// автоподбора после обновления не нашлись бы.
    /// </remarks>
    public static string Strip(string remark) => Split(remark).Name;

    private static string? Decode(string raw)
    {
        if (raw.Length == 0)
            return null;

        var text = raw.Replace('-', '+').Replace('_', '/').TrimEnd('=');
        text = text.PadRight(text.Length + (4 - text.Length % 4) % 4, '=');

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(text)).Trim();

            // Не текст — значит, не base64, а описание словами.
            return decoded.Length > 0 && !decoded.Any(c => char.IsControl(c)) ? decoded : raw;
        }
        catch (FormatException)
        {
            return raw;
        }
    }
}
