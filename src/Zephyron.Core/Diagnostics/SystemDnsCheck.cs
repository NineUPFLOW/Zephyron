using System.Net;
using System.Net.Sockets;

namespace Zephyron.Core.Diagnostics;

/// <summary>
/// Подменяет ли обычный DNS Windows имена, которые блокируют, — до запуска одного десинка.
/// </summary>
/// <remarks>
/// Замер 08.11 у владельца: без программы www.youtube.com, rutracker.org и www.instagram.com
/// через DNS оператора не разрешаются вовсе (NXDOMAIN), а ya.ru — да. Десинк такие имена
/// не чинит: соединения просто нет. Поэтому «авто» при одном десинке смотрит сам и, если
/// подмена есть, отдаёт запросы Windows движку по DoH. Сеть лежит целиком (не разрешается
/// и контрольное имя) — подменой не считается: чинить DNS нечего.
/// </remarks>
public static class SystemDnsCheck
{
    private static readonly string[] Blocked = ["www.youtube.com", "rutracker.org", "www.instagram.com"];
    private const string Control = "ya.ru";

    public static bool Poisoned(Func<string, IPAddress[]?>? resolve = null)
    {
        resolve ??= Resolve;

        if (!Usable(resolve(Control)))
            return false;

        return Blocked.Any(name => !Usable(resolve(name)));
    }

    private static bool Usable(IPAddress[]? addresses) =>
        addresses is { Length: > 0 } && addresses.Any(a => !IPAddress.IsLoopback(a) && !a.Equals(IPAddress.Any) && !Private(a));

    private static bool Private(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork
        && address.GetAddressBytes() is [10, ..] or [192, 168, ..] or [172, >= 16 and <= 31, ..] or [127, ..] or [0, ..];

    private static IPAddress[]? Resolve(string name)
    {
        try
        {
            var task = Dns.GetHostAddressesAsync(name);

            // Молчащий DNS не должен задерживать запуск дольше пары секунд.
            return task.Wait(TimeSpan.FromSeconds(3)) ? task.Result : null;
        }
        catch (Exception ex) when (ex is SocketException or AggregateException or ArgumentException)
        {
            return null;
        }
    }
}
