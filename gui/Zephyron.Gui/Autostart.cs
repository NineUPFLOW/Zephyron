using System.IO;
using Zephyron.Supervisor;

namespace Zephyron.Gui;

/// <summary>Задача автозапуска — одним кодом для выключателя на «Главной» и мастера первого запуска.</summary>
internal static class Autostart
{
    /// <summary>
    /// Трогать планировщик может только сама программа, а не тесты и не отладочный запуск:
    /// 22.11 тесты окна, запущенные от администратора, переписали настоящую задачу
    /// автозапуска на testhost.exe, и при входе в систему программа не поднималась.
    /// </summary>
    private static bool Ours =>
        string.Equals(Path.GetFileName(Environment.ProcessPath), "Zephyron.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Снимает задачу; не из программы — ничего не трогает.</summary>
    public static (bool Ok, string Output) TurnOff() =>
        Ours ? AutostartTask.Remove(AutostartTask.DefaultTaskName) : (false, "не из программы — планировщик не тронут");

    /// <summary>Заводит задачу, если её нет; <c>Ok</c> — задача есть.</summary>
    public static (bool Ok, string Output) TurnOn()
    {
        try
        {
            if (!Ours)
                return (false, "не из программы — планировщик не тронут");

            if (AutostartTask.IsInstalled(AutostartTask.DefaultTaskName))
                return (true, "уже включён");

            if (Environment.ProcessPath is not { } exe)
                return (false, "путь к программе не определён");

            return AutostartTask.Install(new AutostartOptions
            {
                TaskName = AutostartTask.DefaultTaskName,
                ExecutablePath = exe,

                // Задача поднимает интерфейс в трей, а движки он заводит сам —
                // теми же ключами, что и кнопка «Запустить».
                Arguments = TrayIcon.Switch,
                WorkingDirectory = Path.GetFullPath("."),
                // SID, а не имя: см. AutostartTask.CurrentUserId.
                UserId = AutostartTask.CurrentUserId(),
            });
        }
        catch (Exception ex)
        {
            return (false, ex.GetBaseException().Message);
        }
    }
}
