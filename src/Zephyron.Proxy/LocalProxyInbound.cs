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
    /// держит он сам, и это не помеха. Только пока sing-box жив: конфиг остаётся на диске
    /// и после остановки, а порт за это время могла занять другая программа.
    /// </remarks>
    public static string? Problem(int port, string runningConfigPath, Func<int, bool>? busy = null, Func<bool>? engineRunning = null)
    {
        if (!PortAllowed(port))
            return $"порт {port} не подходит: нужен от 1024 до 65535, не занятый движком";

        if (!(busy ?? IsBusy)(port)
            || (InConfig(runningConfigPath, port) && (engineRunning ?? EngineRunning)()))
            return null;

        return $"порт {port} занят другой программой — выберите другой в параметрах туннеля";
    }

    private static bool EngineRunning()
    {
        var running = System.Diagnostics.Process.GetProcessesByName("sing-box");

        foreach (var process in running)
            process.Dispose();

        return running.Length > 0;
    }

    private static string OutcomePath(string configPath) => configPath + ".local-proxy";

    /// <summary>
    /// Запомнить, поднят ли прокси в собранном конфиге: карточка показывает правду,
    /// а не только выключатель (порт занят, нет серверов — входа в конфиге нет).
    /// </summary>
    public static void SaveOutcome(string configPath, bool wanted, int port, string? problem)
    {
        var path = OutcomePath(configPath);

        try
        {
            if (!wanted || InConfig(configPath, port))
                File.Delete(path);
            else
                Zephyron.Core.AtomicFile.WriteAllText(path, $"{port}|{problem ?? "нет работающих серверов выхода"}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Не записалось — карточка покажет выключатель, как раньше.
        }
    }

    /// <summary>
    /// Почему прокси на этом порту не поднят при последней сборке; поднят, не просили
    /// или порт с тех пор сменили — <c>null</c>.
    /// </summary>
    public static string? LastProblem(string configPath, int port)
    {
        try
        {
            var path = OutcomePath(configPath);

            if (!File.Exists(path))
                return null;

            var text = File.ReadAllText(path).Trim();
            var bar = text.IndexOf('|');

            return bar > 0 && text[..bar] == port.ToString(System.Globalization.CultureInfo.InvariantCulture) ? text[(bar + 1)..] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
