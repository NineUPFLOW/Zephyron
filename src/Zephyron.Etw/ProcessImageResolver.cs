using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace Zephyron.Etw;

/// <summary>
/// Определяет путь к исполняемому файлу по PID.
/// </summary>
/// <remarks>
/// ETW отдаёт PID, а правила сопоставляются по имени exe. <c>Process.MainModule</c>
/// для этого не годится: он открывает процесс с лишними правами и падает на
/// защищённых. <c>QueryFullProcessImageNameW</c> обходится
/// PROCESS_QUERY_LIMITED_INFORMATION и работает почти всегда.
/// <para>
/// Результат кэшируется: PID переиспользуются, но в пределах сессии наблюдения
/// вероятность коллизии пренебрежимо мала по сравнению с ценой открытия процесса
/// на каждое соединение.
/// </para>
/// </remarks>
public sealed class ProcessImageResolver
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    // Windows раздаёт PID заново: запись живёт недолго, иначе новый процесс
    // с тем же PID унаследует чужой exe. Неудача не кэшируется вовсе.
    private const long TtlMs = 30_000;

    private readonly ConcurrentDictionary<int, (string Path, long At)> _cache = new();

    public string? Resolve(int processId)
    {
        if (processId <= 0)
            return null;

        var now = Environment.TickCount64;

        if (_cache.TryGetValue(processId, out var hit) && now - hit.At < TtlMs)
            return hit.Path;

        var path = Query(processId);

        if (path is null)
            _cache.TryRemove(processId, out _);
        else
            _cache[processId] = (path, now);

        if (_cache.Count > 4096)
        {
            foreach (var (pid, entry) in _cache)
            {
                if (now - entry.At >= TtlMs)
                    _cache.TryRemove(pid, out _);
            }
        }

        return path;
    }

    private static string? Query(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
            return null;

        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;

            return QueryFullProcessImageNameW(handle, 0, buffer, ref size)
                ? buffer.ToString()
                : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
