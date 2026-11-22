using System.Diagnostics;

namespace Zephyron.Supervisor.Evolution;

/// <summary>
/// Отчёт об обнаруженных системных конфликтах (сторонние VPN, GoodbyeDPI, сторонние драйверы WinDivert).
/// </summary>
public sealed record ConflictReport(
    bool HasConflicts,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> Recommendations);

/// <summary>
/// Проверяет системное окружение на наличие конфликтующих сетевых служб и процессов,
/// способных исказить сетевые пробы или заблокировать установку фильтров WinDivert.
/// </summary>
public static class EnvironmentConflictChecker
{
    private static readonly string[] ConflictingProcessNames =
    [
        "goodbyedpi",
        "byedpi",
        "ciadpi",
        "spoofdpi",
        "zapret-winws",
        "zapret"
    ];

    public static ConflictReport Check()
    {
        var conflicts = new List<string>();
        var recommendations = new List<string>();

        foreach (var name in ConflictingProcessNames)
        {
            try
            {
                var procs = Process.GetProcessesByName(name);
                if (procs.Length > 0)
                {
                    conflicts.Add($"Сторонний процесс обхода DPI: «{name}.exe» (PID: {procs[0].Id})");
                    recommendations.Add($"Рекомендуется завершить процесс «{name}.exe» перед эволюцией во избежание гонки перехвата пакетов в WinDivert.");
                }
            }
            catch
            {
                // Игнорируем ошибки доступа к отдельным процессам
            }
        }

        return new ConflictReport(conflicts.Count > 0, conflicts, recommendations);
    }
}
