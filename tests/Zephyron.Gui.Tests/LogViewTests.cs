using System.IO;
using Zephyron.Gui.Views;
using Xunit;

namespace Zephyron.Gui.Tests;

/// <summary>
/// Журнал: каждый из четырёх журналов читается, «Только ошибки» отбирает,
/// а свежие строки появляются без «Обновить».
/// </summary>
/// <remarks>
/// С 0.16 журнал живёт и панелью главного окна, и окном из «Диагностики»,
/// и перечитывает файл сам. Проверяется на настоящих файлах во временной
/// папке, а не на заглушке: читает он их хвостом и в общем доступе, как
/// движок держит их открытыми.
/// </remarks>
public sealed class LogViewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zy-log-" + Guid.NewGuid().ToString("N"));

    public LogViewTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "runtime"));
        Directory.CreateDirectory(Path.Combine(_root, "logs"));

        Write("runtime/sing-box.log",
            "\u001b[36mINFO\u001b[0m [0] inbound/tun: started",
            "\u001b[31mERROR\u001b[0m [1] outbound/vless: dial tcp: i/o timeout",
            "INFO [2] router: match rule");
        Write("runtime/winws2.log", "winws2: filter 443", "WARN lua: profile 2 skipped");
        Write("runtime/supervisor.log",
            "15:01:02 sing-box: запуск",
            "15:01:20 sing-box: не запустился — процесс завершился с кодом 1",
            "15:01:30 winws2: работает, PID 512");
        Write("logs/gui.log", "окно: открыт раздел «Главная»");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Write(string name, params string[] lines) =>
        File.WriteAllLines(Path.Combine(_root, name), lines);

    private LogView Make() => new() { Root = _root };

    [Theory]
    [InlineData(0, "outbound/vless: dial tcp: i/o timeout")]
    [InlineData(1, "lua: profile 2 skipped")]
    [InlineData(2, "winws2: работает, PID 512")]
    [InlineData(3, "открыт раздел «Главная»")]
    public void Every_journal_is_read(int journal, string expected)
    {
        Sta.Run(() =>
        {
            var view = Make();
            view.Journal = journal;
            view.Reload();

            Assert.Contains(expected, view.Text.Text);
            // Порядковым сравнением: по культуре ESC — «игнорируемый» знак
            // и находится в любой строке.
            Assert.DoesNotContain("\u001b", view.Text.Text, StringComparison.Ordinal);
            Assert.Contains("показаны последние", view.Status.Text);
        });
    }

    [Fact]
    public void Errors_only_keeps_engine_and_supervisor_failures()
    {
        Sta.Run(() =>
        {
            var view = Make();

            view.Journal = 0;
            view.ErrorsOnly = true;
            Assert.Equal("ERROR [1] outbound/vless: dial tcp: i/o timeout", view.Text.Text);
            Assert.Equal("Все строки", view.ErrorsButton.Content);

            // Супервизор пишет по-русски — его сбой тоже ошибка.
            view.Journal = 2;
            view.Reload();
            Assert.Equal("15:01:20 sing-box: не запустился — процесс завершился с кодом 1", view.Text.Text);

            view.ErrorsOnly = false;
            Assert.Contains("работает, PID 512", view.Text.Text);
            Assert.Equal("Только ошибки", view.ErrorsButton.Content);
        });
    }

    [Fact]
    public void A_missing_journal_says_so()
    {
        File.Delete(Path.Combine(_root, "runtime", "winws2.log"));

        Sta.Run(() =>
        {
            var view = Make();
            view.Journal = 1;
            view.Reload();

            Assert.Equal(string.Empty, view.Text.Text);
            Assert.StartsWith("Журнала нет", view.Status.Text);
        });
    }

    [Fact]
    public void Following_picks_up_new_lines()
    {
        Sta.Run(() =>
        {
            var view = Make();
            view.Journal = 0;
            view.Reload();

            File.AppendAllLines(Path.Combine(_root, "runtime", "sing-box.log"), ["INFO [3] dns: exchange ok"]);
            view.Reload(follow: true);

            Assert.EndsWith("INFO [3] dns: exchange ok", view.Text.Text);
        });
    }
}
