using System.IO;
using Zephyron.Core;

namespace Zephyron.Gui;

/// <summary>
/// Распакованные библиотеки прошлых версий во временной папке — убрать.
/// </summary>
/// <remarks>
/// Однофайловая сборка распаковывает себя в <c>%TEMP%\.net\Zephyron\&lt;хеш&gt;</c>
/// (InstallRoot.AppFolder). У каждой версии свой хеш, и после обновления прежняя
/// папка — около 170 МБ — оставалась бы навсегда. Занятые файлы (старый процесс ещё
/// не вышел) пропускаются: уберёт следующий запуск.
/// </remarks>
internal static class BundleFolders
{
    public static void SweepOld() => Task.Run(() =>
    {
        try
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));

            // Не распакованы (запуск из папки сборки) — чистить нечего.
            if (string.Equals(current.FullName + Path.DirectorySeparatorChar, InstallRoot.AppFolder, StringComparison.OrdinalIgnoreCase)
                || current.Parent is not { Name: "Zephyron", Parent.Name: ".net" } parent)
                return;

            foreach (var old in parent.EnumerateDirectories())
            {
                if (string.Equals(old.FullName, current.FullName, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    old.Delete(recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Занято старым процессом — в следующий раз.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Нет доступа к временной папке — не наша забота.
        }
    });
}
