namespace Zephyron.Proxy;

/// <summary>
/// Файл-записка окна супервизору: «выбранный сервер сменён на ходу».
/// </summary>
/// <remarks>
/// Супервизор — отдельный процесс, и сторож выхода держит прежний закреплённый
/// сервер в памяти: смени окно селектор через Clash API, сторож «вернул» бы
/// старый. Записка — тот же канал-файл, что у остальных (runtime/*.json); новый
/// IPC ради одной строки не нужен. Пустой файл — автоподбор.
/// </remarks>
public static class ExitPin
{
    public static string DefaultPath => Path.Combine("runtime", "exit-pin.txt");

    /// <summary>Оставить записку; <c>null</c> — автоподбор.</summary>
    public static void Write(string path, string? tag)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var before = File.GetLastWriteTimeUtc(path);
        // Атомарно: супервизор читает записку по своему таймеру и не должен застать её пустой.
        Zephyron.Core.AtomicFile.WriteAllText(path, tag ?? string.Empty);

        // Две записки в одну долю секунды мтайм не различит — штамп обязан расти.
        if (File.GetLastWriteTimeUtc(path) <= before)
            File.SetLastWriteTimeUtc(path, before.AddMilliseconds(1));
    }

    /// <summary>Штамп записки сейчас: записки старше запуска супервизора не в счёт.</summary>
    public static DateTime Stamp(string path) => File.GetLastWriteTimeUtc(path);

    /// <summary>Новая ли записка с прошлого раза; тогда — что в ней.</summary>
    public static bool TryTake(string path, ref DateTime stamp, out string? tag)
    {
        tag = null;

        try
        {
            var now = File.GetLastWriteTimeUtc(path);

            if (now <= stamp)
                return false;

            var text = File.ReadAllText(path).Trim();
            stamp = now;
            tag = text.Length == 0 ? null : text;
            return true;
        }
        catch (IOException)
        {
            // Пишут прямо сейчас — возьмём на следующем опросе.
            return false;
        }
    }
}
