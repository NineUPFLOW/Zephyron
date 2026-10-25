using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Xunit;
using Zephyron.Core;

namespace Zephyron.Gui.Tests;

/// <summary>
/// Оболочка «Сигнала»: окно, пересоздаваемое из трея, не держится статикой;
/// выключатель движения доходит до шаблонов; контракт FieldPulse.
/// </summary>
[Collection(LeaksFirst.Name)]
public sealed class MainWindowShellTests
{
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference OpenAndClose()
    {
        Motion.Use(new AppSettings { Effects = EffectsProfile.Full });

        var window = new MainWindow { ShowActivated = false };
        window.Show();
        Pump();

        // Движение сменилось при открытом окне — подписка Motion.Changed жива.
        Motion.Use(new AppSettings { Effects = EffectsProfile.Lite });
        Pump();

        var weak = new WeakReference(window);

        // Крестик прячет в трей; по-настоящему окно закрывает выход.
        App.Exiting = true;

        try
        {
            window.Close();
        }
        finally
        {
            App.Exiting = false;
        }

        Pump();
        return weak;
    }

    [Fact]
    public void The_window_is_collected_after_three_open_close_cycles() => Sta.Run(() =>
    {
        var refs = new List<WeakReference>();

        for (int i = 0; i < 3; i++)
            refs.Add(OpenAndClose());

        // До 3 с с паузами: «Главная» на загрузке шлёт фоновые запросы (движки, сеть),
        // и их продолжения держат раздел, а через Parent и окно, ещё десятки миллисекунд
        // после закрытия — в общем прогоне под нагрузкой дольше. Подписка или таймер
        // держали бы окно всегда, и за 3 с проверка их по-прежнему ловит.
        var clock = System.Diagnostics.Stopwatch.StartNew();

        while (refs.Any(r => r.IsAlive) && clock.Elapsed < TimeSpan.FromSeconds(3))
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            Pump();
            Thread.Sleep(50);
            Pump();
        }

        Assert.All(refs, r => Assert.False(r.IsAlive, "окно удержано статической подпиской или таймером"));
    });

    [Fact]
    public void Template_switch_follows_Motion() => Sta.Run(() =>
    {
        Motion.Use(new AppSettings { Effects = EffectsProfile.Full });

        var bar = new ProgressBar { IsIndeterminate = true };
        var window = new Window { Content = bar, ShowActivated = false };
        window.Show();
        Pump();

        try
        {
            Assert.False(Motion.GetStill(bar));

            Motion.Use(new AppSettings { Animations = false });
            Pump();
            Assert.True(Motion.GetStill(bar));

            Motion.Use(new AppSettings { Effects = EffectsProfile.Lite });
            Pump();
            Assert.True(Motion.GetStill(bar));

            Motion.Use(new AppSettings { Effects = EffectsProfile.Balanced });
            Pump();
            Assert.False(Motion.GetStill(bar));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void FieldPulse_overrides_the_dot_mode_until_released() => Sta.Run(() =>
    {
        Motion.Use(new AppSettings { Effects = EffectsProfile.Full });

        var window = new MainWindow { ShowActivated = false };
        var field = (ParticleField)window.FindName("Field");

        try
        {
            window.FieldPulse(FieldMode.Starting, 12.5);
            Assert.Equal(FieldMode.Starting, field.Mode);
            Assert.Equal(12.5, field.Mbps);

            // null — вернуть режим по файлу состояния (здесь — «выключен»).
            window.FieldPulse(null, null);
            Assert.NotEqual(FieldMode.Starting, field.Mode);
            Assert.Null(field.Mbps);
        }
        finally
        {
            App.Exiting = true;
            window.Close();
            App.Exiting = false;
        }
    });

    [Theory]
    [InlineData("Faint", FieldMode.Off)]
    [InlineData("Success", FieldMode.Running)]
    [InlineData("Warn", FieldMode.Degraded)]
    [InlineData("Danger", FieldMode.Failed)]
    public void Dot_key_maps_to_field_mode(string key, FieldMode mode) => Assert.Equal(mode, MainWindow.ModeOf(key));
}
