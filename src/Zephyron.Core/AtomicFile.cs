using System.Text;

namespace Zephyron.Core;

/// <summary>
/// Запись файла через временный соседний: обрыв посреди записи не оставит
/// от настроек половину файла.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents, Encoding? encoding = null)
    {
        var temp = TempPath(path);

        try
        {
            File.WriteAllText(temp, contents, encoding ?? new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    public static async Task WriteAllTextAsync(string path, string contents, Encoding? encoding = null, CancellationToken cancellationToken = default)
    {
        var temp = TempPath(path);

        try
        {
            await File.WriteAllTextAsync(temp, contents, encoding ?? new UTF8Encoding(false), cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    // Рядом с целью: Move в пределах одного тома — атомарная замена.
    private static string TempPath(string path) => path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
