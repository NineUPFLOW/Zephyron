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
