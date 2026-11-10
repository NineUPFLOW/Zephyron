using System.IO;
using Zephyron.Gui.Views;
using Xunit;

namespace Zephyron.Gui.Tests;

/// <summary>Расчёты новой «Главной»: шкала графика, хвост журнала и цвет события.</summary>
public sealed class NexusHomeTests
{
    [Theory]
    [InlineData(0.3, 0.5)]
    [InlineData(1, 1)]
    [InlineData(1.2, 2)]
    [InlineData(37, 50)]
    [InlineData(80, 100)]
    [InlineData(0, 1)]
    public void The_chart_scale_tops_out_at_a_round_number(double peak, double top)
    {
        Assert.Equal(top, NetChart.Nice(peak), 6);
    }

    [Fact]
    public void The_events_are_the_last_lines_of_the_journal()
    {
        var path = Path.Combine(Path.GetTempPath(), $"zephyron-tail-{Guid.NewGuid():N}.log");

        try
        {
            File.WriteAllLines(path, Enumerable.Range(1, 500).Select(i => $"[12:00:{i % 60:00}] окно: строка {i}"));

            var tail = StatusView.Tail(path, bytes: 2048, count: 3);

            Assert.Equal(["[12:00:18] окно: строка 498", "[12:00:19] окно: строка 499", "[12:00:20] окно: строка 500"], tail);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("[15:44:44] окно: уведомление", "15:44:44", "окно:", "уведомление")]
    [InlineData("2026-11-09 15:46:22 winws2: не запустился — код 5: windivert: error", "15:46:22", "winws2:", "не запустился — код 5: windivert: error")]
    [InlineData("2026-11-09 15:47:13 супервизор остановлен", "15:47:13", "надзор:", "супервизор остановлен")]
    public void Both_journal_formats_reach_the_feed(string line, string time, string source, string message)
    {
        Assert.Equal((time, source, message), StatusView.Split(line));
    }

    [Theory]
    [InlineData("2026-11-10 16:43:20 sing-box: работает, PID 18204", "Туннель (VPN):", "работает")]
    [InlineData("2026-11-10 16:43:20 winws2: работает, PID 5464", "Десинк:", "работает")]
    [InlineData("2026-11-10 16:43:20 супервизор остановлен", "Zephyron:", "обход выключен")]
    [InlineData("[16:43:19] движки: запуск — кнопка «Запустить» на «Главной»", "Zephyron:", "обход включён кнопкой")]
    [InlineData("[16:43:19] движки: остановка — обновление программы", "Zephyron:", "обход выключен (обновление программы)")]
    [InlineData("[16:35:26] видео YouTube: узлы под фильтром ожили под малым окном, видео идёт без VPN: rr1---sn-a.googlevideo.com, rr1---sn-b.googlevideo.com",
        "YouTube:", "починены узлы видео (2), ролики идут без VPN")]
    [InlineData("[10:00:00] окно: уведомление после обновления до 1.4.5", "Zephyron:", "программа обновлена до 1.4.5")]
    public void Feed_speaks_plainly(string line, string who, string what)
    {
        var (_, source, message) = StatusView.Split(line)!.Value;
        Assert.Equal((who, what), StatusView.Plain(source, message));
    }

    [Theory]
    [InlineData("2026-11-10 16:43:18 Свой рецепт «pass»: C:\z\runtime\desync\pass.txt")]
    [InlineData("2026-11-10 16:43:18 Пресет: Universal V12 Lite (412 аргументов)")]
    [InlineData("2026-11-10 16:43:18 Десинк не перехватывает трафик в туннель: подменные адреса 198.18.0.0/15")]
    [InlineData("2026-11-10 16:43:18 супервизор запущен, PID 4242")]
    [InlineData("2026-11-10 16:43:18 winws2: запуск")]
    [InlineData("[16:43:18] пин: проверка пинов (сама): просмотрено 12, менять нечего")]
    public void Kitchen_lines_stay_in_the_full_journal(string line)
    {
        var (_, source, message) = StatusView.Split(line)!.Value;
        Assert.Null(StatusView.Plain(source, message));
    }

    [Fact]
    public void Window_stall_notes_stay_out_of_the_feed()
    {
        Assert.True(StatusView.IsStallNote("[15:52:23] окно: поток окна был занят 1842 мс, раздел «VPN»"));
        Assert.False(StatusView.IsStallNote("[15:52:23] окно: уведомление после обновления"));
    }

    [Fact]
    public void Same_autopick_reason_is_said_once()
    {
        var notes = new[]
        {
            "пресет: winws2 не поднялся",
            "рецепт «a»: winws2 не поднялся",
            "рецепт «b: c»: winws2 не поднялся",
            "рецепт «d»: другая причина",
        };

        Assert.Equal(["winws2 не поднялся — так у 3 проб", "рецепт «d»: другая причина"], AutoPickWindow.Squeeze(notes));
    }

    [Fact]
    public void A_missing_journal_means_no_events()
    {
        Assert.Empty(StatusView.Tail(Path.Combine(Path.GetTempPath(), "нет-такого-журнала.log"), 1024, 5));
    }

    [Fact]
    public void The_tray_badge_follows_the_bypass_state()
    {
        Assert.Equal(FieldMode.Off, TrayIcon.StateOf(new TrayStatus(Running: false, AllHealthy: false, Tunnel: null, Desync: null, Busy: false)));
        Assert.Equal(FieldMode.Running, TrayIcon.StateOf(new TrayStatus(Running: true, AllHealthy: true, Tunnel: null, Desync: null, Busy: false)));
        Assert.Equal(FieldMode.Degraded, TrayIcon.StateOf(new TrayStatus(Running: true, AllHealthy: false, Tunnel: null, Desync: null, Busy: false)));
        Assert.Equal(FieldMode.Failed, TrayIcon.StateOf(new TrayStatus(Running: true, AllHealthy: false, Tunnel: Zephyron.Supervisor.ServiceHealth.Dead, Desync: null, Busy: false)));
        Assert.Equal(FieldMode.Starting, TrayIcon.StateOf(new TrayStatus(Running: false, AllHealthy: false, Tunnel: null, Desync: null, Busy: false, Starting: true)));
    }

    [Theory]
    [InlineData("winws2 упал, перезапускаю", "Danger")]
    [InlineData("sing-box не ответил на проверку", "Warn")]
    [InlineData("движки запущены", "Success")]
    [InlineData("конфиг собран", "Faint")]
    public void An_event_is_tinted_by_its_meaning(string message, string tone)
    {
        Assert.Equal(tone, StatusView.Tone(message));
    }
}
