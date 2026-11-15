using System.Diagnostics;
using System.IO;

namespace Zephyron.Gui;

/// <summary>
/// Очистка кеша Discord: после смены маршрута или десинка он держит старые соединения.
/// </summary>
/// <remarks>
/// Трогаются ровно три папки кеша каждого варианта клиента. Local Storage и
/// настройки не трогаются никогда: в них вход в аккаунт.
/// </remarks>
internal static class DiscordCache
{
    private static readonly string[] Clients = ["discord", "discordptb", "discordcanary"];

    private static readonly string[] CacheFolders = ["Cache", "Code Cache", "GPUCache"];

    private static readonly string[] ProcessNames = ["Discord", "DiscordPTB", "DiscordCanary"];

    /// <summary>Папки кеша под <paramref name="appData"/>; чистая логика путей.</summary>
    public static IReadOnlyList<string> Folders(string appData) =>
        [.. Clients.SelectMany(client => CacheFolders.Select(folder => Path.Combine(appData, client, folder)))];

    /// <summary>Закрывает Discord и чистит кеш; возвращает текст для статуса.</summary>
    public static async Task<string> ClearAsync(string? appData = null, bool close = true)
    {
        appData ??= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        return await Task.Run(() =>
        {
            bool wasRunning = close && Close();

            int removed = 0, locked = 0;

            foreach (var folder in Folders(appData).Where(Directory.Exists))
            {
                // Занятый файл не повод бросать остальное: считаем и идём дальше.
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList())
                {
                    try
                    {
                        File.Delete(file);
                        removed++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        locked++;
                    }
                }

                foreach (var dir in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories)
                             .OrderByDescending(d => d.Length).ToList())
                {
                    try
                    {
                        Directory.Delete(dir);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }

            Journal.Write("discord", $"кеш очищен: файлов {removed}, занятых {locked}");

            return removed + locked == 0
                ? "Кеша Discord не нашлось, чистить нечего."
                : $"Кеш Discord очищен: файлов удалено {removed}"
                    + (locked > 0 ? $", занятых осталось {locked}" : string.Empty)
                    + (wasRunning ? ". Запустите Discord снова." : ".");
        });
    }

    /// <returns>Был ли Discord запущен.</returns>
    private static bool Close()
    {
        var running = ProcessNames.SelectMany(Process.GetProcessesByName).ToList();

        foreach (var process in running)
        {
            try
            {
                process.CloseMainWindow();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);

        foreach (var process in running)
        {
            try
            {
                var left = deadline - DateTime.UtcNow;

                if (left <= TimeSpan.Zero || !process.WaitForExit(left))
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return running.Count > 0;
    }
}
