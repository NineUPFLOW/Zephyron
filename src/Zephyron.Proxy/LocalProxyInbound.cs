using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Zephyron.Proxy;

/// <summary>Общее для локального прокси: теги, допустимый порт и проверка, что порт можно занять.</summary>
public static class LocalProxyInbound
{
    public const string InboundTag = "local-proxy";

    /// <summary>Группа выхода: те же серверы, что у туннеля, но без прямого выхода.</summary>
    public const string ExitTag = "proxy-exit";

    public const int DefaultPort = 2080;

    // Порты служебных входов движка — на них прокси сесть не должен.
    private static readonly int[] Reserved = [9090, SingBoxOptions.DefaultHealthPort, 21080];

    public static string Address(int port) => $"127.0.0.1:{port}";

    public static bool PortAllowed(int port) => port is >= 1024 and <= 65535 && !Reserved.Contains(port);

    /// <summary>Занят ли порт на петле: пробуем занять сами.</summary>
    public static bool IsBusy(int port)
    {
        try
        {
            var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
            probe.Stop();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    /// <summary>Есть ли во входах конфига наш прокси на этом порту.</summary>
    public static bool InConfig(string configPath, int port)
    {
        try
        {
            if (!File.Exists(configPath))
                return false;

            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));

            if (!doc.RootElement.TryGetProperty("inbounds", out var inbounds) || inbounds.ValueKind != JsonValueKind.Array)
                return false;

            return inbounds.EnumerateArray().Any(i =>
                i.TryGetProperty("tag", out var tag) && tag.GetString() == InboundTag
                && i.TryGetProperty("listen_port", out var p) && p.TryGetInt32(out var n) && n == port);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Что мешает поднять вход; <c>null</c> — можно.
    /// </summary>
    /// <remarks>
    /// Занятый порт валит sing-box целиком, вместе с туннелем, поэтому проверяем до запуска.
    /// «Наш» определяет конфиг, с которым движок работает сейчас, а не настройка: порт
    /// держит он сам, и это не помеха.
    /// </remarks>
    public static string? Problem(int port, string runningConfigPath, Func<int, bool>? busy = null)
    {
        if (!PortAllowed(port))
            return $"порт {port} не подходит: нужен от 1024 до 65535, не занятый движком";

        if (!(busy ?? IsBusy)(port) || InConfig(runningConfigPath, port))
            return null;

        return $"порт {port} занят другой программой — выберите другой в параметрах туннеля";
    }
}
