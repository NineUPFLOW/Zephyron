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
        if (Process.GetProcessesByName("sing-box").Length > 0)
            return "адаптер от прошлого запуска на месте, но sing-box жив — не трогаю";

        foreach (var guid in Find())
            Remove(guid);

        return Find().Count == 0
            ? "адаптер от прошлого запуска не ушёл сам — удалён"
            : "адаптер от прошлого запуска не удалось убрать: уберите sing-tun Tunnel в диспетчере устройств";
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

            process?.WaitForExit(15_000);
        }
        catch (Exception)
        {
            // Не вышло — новый sing-box скажет об этом своим отказом.
        }
    }
}
