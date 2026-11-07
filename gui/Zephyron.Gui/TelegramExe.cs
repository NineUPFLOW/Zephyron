using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Zephyron.Gui;

/// <summary>Где Telegram Desktop (и его сборки) — чтобы отдать ему ссылку прокси.</summary>
internal static class TelegramExe
{
    private static readonly string[] Names = ["Telegram", "AyuGram", "Kotatogram", "64Gram", "Materialgram"];

    /// <summary>Путь к exe запущенного Telegram; не запущен — <c>null</c>.</summary>
    public static string? Running()
    {
        foreach (var name in Names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        if (process.MainModule?.FileName is { } path && File.Exists(path))
                            return path;
                    }
                    catch (Exception)
                    {
                        // Чужой процесс без доступа — смотрим следующий.
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Есть ли программа по записи tg:// и на месте ли она; записи нет — <c>null</c>.</summary>
    public static bool? Registered()
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(@"tg\shell\open\command");
            if (key?.GetValue(null) is not string command)
                return null;

            var trimmed = command.TrimStart();
            var path = trimmed.StartsWith('"') ? trimmed[1..].Split('"')[0] : trimmed.Split(' ')[0];

            return File.Exists(Environment.ExpandEnvironmentVariables(path));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
