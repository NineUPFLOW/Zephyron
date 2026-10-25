using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;
using Zephyron.Core;

namespace Zephyron.Gui.Tests;

/// <summary>
/// Движок эффектов: профиль, рассылка Changed и жизнь подписки слоя частиц.
/// Реальный кадр монитора не нужен — цикл проверяется по флагу IsRunning.
/// </summary>
public class ParticleFieldTests
{
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static (Window Window, ParticleField Field) Show(AppSettings settings)
    {
        Motion.Use(settings);

        var field = new ParticleField(1) { ActiveOverride = true };
        var window = new Window { Width = 400, Height = 300, Content = field, ShowActivated = false };
        window.Show();
        Pump();
        return (window, field);
    }

    [Fact]
    public void Profile_is_Lite_when_animations_off() => Sta.Run(() =>
    {
        Motion.Use(new AppSettings { Animations = false, Effects = EffectsProfile.Full });
        Assert.Equal(EffectsProfile.Lite, Motion.Profile);
    });

    [Fact]
    public void Refresh_raises_Changed() => Sta.Run(() =>
    {
        int n = 0;
        EventHandler h = (_, _) => n++;
        Motion.Changed += h;

        try
        {
            Motion.Refresh();
            Assert.Equal(1, n);
        }
        finally
        {
            Motion.Changed -= h;
        }
    });

    [Fact]
    public void Lite_field_does_not_subscribe() => Sta.Run(() =>
    {
        var (window, field) = Show(new AppSettings { Effects = EffectsProfile.Lite });

        try
        {
            Assert.False(field.IsRunning);
            Assert.Equal(0, field.ActiveNodes);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Field_runs_while_loaded_and_stops_on_unload_and_Lite() => Sta.Run(() =>
    {
        var (window, field) = Show(new AppSettings { Effects = EffectsProfile.Full });

        try
        {
            Assert.True(field.IsRunning);
            Assert.Equal(110, field.ActiveNodes);

            Motion.Use(new AppSettings { Effects = EffectsProfile.Balanced });
            Assert.Equal(50, field.ActiveNodes);

            Motion.Use(new AppSettings { Effects = EffectsProfile.Lite });
            Assert.False(field.IsRunning);

            Motion.Use(new AppSettings { Effects = EffectsProfile.Full });
            Assert.True(field.IsRunning);

            window.Content = null;
            Pump();
            Assert.False(field.IsRunning);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Closed_window_releases_field() => Sta.Run(() =>
    {
        WeakReference Make()
        {
            var (window, field) = Show(new AppSettings { Effects = EffectsProfile.Full });
            Assert.True(field.IsRunning);
            window.Close();
            Pump();
            return new WeakReference(field);
        }

        var weak = Make();

        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(weak.IsAlive);
    });

    [Fact]
    public void Node_count_follows_profile()
    {
        Assert.Equal(110, ParticleField.NodeCount(EffectsProfile.Full));
        Assert.Equal(50, ParticleField.NodeCount(EffectsProfile.Balanced));
        Assert.Equal(0, ParticleField.NodeCount(EffectsProfile.Lite));
    }

    [Fact]
    public void Input_on_zero_size_does_not_throw() => Sta.Run(() =>
    {
        Motion.Use(new AppSettings { Effects = EffectsProfile.Full });
        var field = new ParticleField(1);
        field.Pointer(new Point(5, 5));
        field.Burst(new Point(5, 5));
        field.Leave();
        field.Mode = FieldMode.Running;
    });

    [Fact]
    public void Render_cost_of_full_field()
    {
        Sta.Run(() =>
        {
            Motion.Use(new AppSettings { Effects = EffectsProfile.Full });
            var (window, field) = Show(new AppSettings { Effects = EffectsProfile.Full });

            try
            {
                field.Mode = FieldMode.Running;
                field.Mbps = 100;
                field.Pointer(new Point(200, 150));
                var visual = new DrawingVisual();
                var sw = Stopwatch.StartNew();

                // Один кадр — это OnRender; через рендер в битмап он вызывается честно.
                var bmp = new RenderTargetBitmap(400, 300, 96, 96, PixelFormats.Pbgra32);

                for (int i = 0; i < 30; i++)
                {
                    field.InvalidateVisual();
                    field.UpdateLayout();
                    bmp.Render(field);
                }

                Console.WriteLine($"ParticleField Full: {sw.Elapsed.TotalMilliseconds / 30:F2} мс на кадр (с растеризацией в битмап)");
                GC.KeepAlive(visual);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
