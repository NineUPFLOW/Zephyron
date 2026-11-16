using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;

namespace Zephyron.Supervisor;

/// <summary>
/// TUN-адаптер, переживший свой sing-box, — убрать до нового запуска.
/// </summary>
/// <remarks>
/// <para>
/// Отчёт 26.10: sing-box упал вместе с winws2, и пять перезапусков подряд
/// падали через пять секунд с «configure tun interface: set ipv4 address:
/// The object already exists» (или «Cannot create a file when that file
/// already exists»). Убитый жёстко sing-box адаптер не убирает — wintun
/// рассчитывает на спокойный выход, — и новый не может завести свой.
/// Надзор сдался, и туннель стоял до ручного запуска.
/// </para>
/// <para>
/// Наш — только адаптер sing-tun с нашим именем или нашим адресом: описание
/// «sing-tun Tunnel» у всякого клиента на sing-box, и чужой трогать нельзя.
/// </para>
/// </remarks>
public static class GhostTunnel
{
    public const string OurName = "zephyron0";

    /// <summary>Адрес из <c>SingBoxOptions.TunAddress</c>.</summary>
    public static readonly IPAddress OurAddress = IPAddress.Parse("172.19.0.1");

    public static bool IsOurs(string name, string description, IEnumerable<IPAddress> addresses) =>
        description.Contains("sing-tun", StringComparison.OrdinalIgnoreCase)
        && (string.Equals(name, OurName, StringComparison.OrdinalIgnoreCase) || addresses.Contains(OurAddress));

    /// <summary>GUID-ы наших адаптеров, что есть в системе сейчас.</summary>
    public static IReadOnlyList<string> Find()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => IsOurs(n.Name, n.Description, n.GetIPProperties().UnicastAddresses.Select(a => a.Address)))
                .Select(n => n.Id)
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    /// <summary>
    /// Ждёт, пока адаптер уйдёт сам; не ушёл, а движка нет — удаляет устройство.
    /// </summary>
    /// <returns>Что случилось, для журнала; <c>null</c> — адаптера не было.</returns>
    public static async Task<string?> ClearAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        if (Find().Count == 0)
            return null;

        var deadline = DateTime.UtcNow + wait;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500, cancellationToken);

            if (Find().Count == 0)
                return "адаптер от прошлого запуска ушёл сам";
        }

        // Живой sing-box держит адаптер законно — может, это не наш; не трогаем.
        var singBoxes = Process.GetProcessesByName("sing-box");

        foreach (var p in singBoxes)
            p.Dispose();

        if (singBoxes.Length > 0)
            return "адаптер от прошлого запуска на месте, но sing-box жив — не трогаю";

        foreach (var guid in Find())
            Remove(guid);

        return Find().Count == 0
            ? "адаптер от прошлого запуска не ушёл сам — удалён"
            : "старый адаптер туннеля не отпускает Windows — туннель поднимется после перезагрузки компьютера; десинк работает и сейчас";
    }

    /// <summary>Отказ sing-box на создании TUN-адаптера.</summary>
    public static bool IsTunFailure(string? fatal) =>
        fatal is not null
        && (fatal.Contains("configure tun", StringComparison.OrdinalIgnoreCase)
            || fatal.Contains("wintun", StringComparison.OrdinalIgnoreCase)
            || fatal.Contains("tun", StringComparison.OrdinalIgnoreCase) && fatal.Contains("already exists", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Убирает отсутствующие туннельные устройства (Wintun, sing-tun, WireGuard) — следы
    /// удалённых или упавших VPN-клиентов.
    /// </summary>
    /// <remarks>
    /// Письмо пользователя 15.11: туннель не создавался, пока он не удалил старые адаптеры
    /// Wintun от прежних клиентов. Такой «призрак» хранит свой адрес (у клиентов на sing-box
    /// часто тот же 172.19.0.1), и новый адаптер падает с «already exists». Трогаем только
    /// устройства, которых сейчас нет в системе: подключённый адаптер работающего клиента
    /// не задевается, а отсутствующий клиент заведёт себе новый при следующем запуске.
    /// </remarks>
    /// <returns>Сколько устройств удалено; -1 — проверить не удалось.</returns>
    public static int RemovePhantoms()
    {
        const string script =
            "$n = 0; Get-PnpDevice -Class Net -ErrorAction SilentlyContinue "
            + "| Where-Object { -not $_.Present -and $_.FriendlyName -match 'Wintun|sing-tun|WireGuard' } "
            + "| ForEach-Object { pnputil /remove-device $_.InstanceId | Out-Null; if ($LASTEXITCODE -eq 0) { $n++ } }; $n";

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", script },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null)
                return -1;

            var output = process.StandardOutput.ReadToEndAsync();

            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                return -1;
            }

            return int.TryParse(output.Result.Trim().Split('\n').LastOrDefault()?.Trim(), out var removed) ? removed : -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static void Remove(string guid)
    {
        // GUID приходит из NetworkInterface.Id — это не ввод человека.
        var script = $"Get-NetAdapter -IncludeHidden | Where-Object InterfaceGuid -eq '{guid}' "
            + "| ForEach-Object { pnputil /remove-device $_.PnPDeviceID }";

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", script },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            // Завис — не оставляем powershell висеть до конца сеанса.
            if (process is { } p && !p.WaitForExit(15_000))
                p.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Не вышло — новый sing-box скажет об этом своим отказом.
        }
    }
}
