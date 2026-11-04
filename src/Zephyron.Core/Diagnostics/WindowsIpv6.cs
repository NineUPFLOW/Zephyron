using Microsoft.Win32;

namespace Zephyron.Core.Diagnostics;

/// <summary>
/// Выключен ли IPv6 в Windows целиком — так, что новому адаптеру (нашему TUN) его не дать.
/// </summary>
/// <remarks>
/// Бит 0x10 у <c>Tcpip6\Parameters\DisabledComponents</c> — «выключить на всех нетуннельных адаптерах»;
/// Wintun под него попадает. 0xffffffff приходит как −1. Бит 0x01 (Teredo) и 0x20 («предпочитать IPv4»)
/// выключенным не считаются. Только чтение. Значение действует после перезагрузки Windows.
/// </remarks>
public static class WindowsIpv6
{
    private const string Key = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters";

    private const uint NonTunnel = 0x10;

    /// <summary>Выключен ли IPv6 для адаптеров вроде нашего TUN.</summary>
    public static bool Disabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(Key);
            return Disables(key?.GetValue("DisabledComponents"));
        }
        catch (Exception)
        {
            // Не прочли — считаем, что IPv6 есть: так было всегда.
            return false;
        }
    }

    /// <summary>Значение <c>DisabledComponents</c> выключает IPv6 нашему адаптеру.</summary>
    public static bool Disables(object? value) => value switch
    {
        int number => (unchecked((uint)number) & NonTunnel) != 0,
        long number => (unchecked((uint)number) & NonTunnel) != 0,
        _ => false,
    };
}
