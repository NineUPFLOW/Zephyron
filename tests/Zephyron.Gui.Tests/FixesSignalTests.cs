using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Xunit;
using Zephyron.Core;
using Zephyron.Gui.Views;
using Zephyron.Supervisor;

namespace Zephyron.Gui.Tests;

/// <summary>Исправления редизайна «Сигнал»: ореол после Lite, срок «Запуска», текст «Сбой», тон событий, метка трея, черта, слой частиц.</summary>
public sealed class FixesSignalTests
{
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Close(MainWindow window)
    {
        App.Exiting = true;

        try
        {
            window.Close();
        }
        finally
        {
            App.Exiting = false;
        }
    }

    private static void ShowState(MainWindow window) =>
        typeof(MainWindow).GetMethod("ShowState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);

    // 1
    [Fact]
    public void Glow_returns_after_starting_in_Lite() => Sta.Run(() =>
    {
        try
        {
            Motion.Use(new AppSettings { Effects = EffectsProfile.Lite });

            var btn = new Button { Content = "x" };
            Glow.SetRadius(btn, 10);
            var window = new Window { Content = btn, ShowActivated = false };
            window.Show();
            Pump();

            try
            {
                Assert.Null(btn.Effect);

                Motion.Use(new AppSettings { Effects = EffectsProfile.Balanced });
                Pump();

                Assert.True(btn.Effect is DropShadowEffect, "ореол не вернулся после Lite");
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Motion.Use(new AppSettings());
        }
    });

    // 2
    [Fact]
    public void FieldPulse_Starting_outlives_StartupPatience_until_released() => Sta.Run(() =>
    {
        Motion.Use(new AppSettings { Effects = EffectsProfile.Full });

        var window = new MainWindow { ShowActivated = false };
        var field = (ParticleField)window.FindName("Field");
        var t0 = DateTime.UtcNow;
        var now = t0;
        window.Now = () => now;

        try
        {
            window.FieldPulse(FieldMode.Starting, null);

            // Дольше прежних 30 с и дольше терпения «Главной» (90 с) — без продлений.
            now = t0.AddSeconds(95);
            ShowState(window);
            Assert.Equal(FieldMode.Starting, field.Mode);

            // Тик ShowStarting продлевает срок.
            window.FieldPulse(FieldMode.Starting, null);
            now = t0.AddSeconds(200);
            ShowState(window);
            Assert.Equal(FieldMode.Starting, field.Mode);

            window.FieldPulse(null, null);
            Assert.NotEqual(FieldMode.Starting, field.Mode);
        }
        finally
        {
            Close(window);
        }
    });

    // 2б: уход «Главной» снимает навязанный режим, хотя в Unloaded окна у неё уже нет.
    [Fact]
    public void Leaving_home_releases_the_forced_mode() => Sta.Run(() =>
    {
        Motion.Use(new AppSettings { Effects = EffectsProfile.Full });

        var window = new MainWindow { ShowActivated = false };
        var field = (ParticleField)window.FindName("Field");
        var section = (ContentControl)window.FindName("Section");
        window.Show();
        Pump();

        try
        {
            Assert.IsType<StatusView>(section.Content);

            window.FieldPulse(FieldMode.Starting, 3);
            Assert.Equal(FieldMode.Starting, field.Mode);

            section.Content = null;
            Pump();

            Assert.NotEqual(FieldMode.Starting, field.Mode);
            Assert.Null(field.Mbps);
        }
        finally
        {
            Close(window);
        }
    });

    // 3
    [Fact]
    public void Failed_hint_names_the_dead_engines_and_does_not_soften() => Sta.Run(() =>
    {
        var services = new[]
        {
            new ServiceState { Name = "winws2", Health = ServiceHealth.Dead },
            new ServiceState { Name = "sing-box", Health = ServiceHealth.Dead },
        };

        var hint = StatusView.BrokenHint(services);
        Assert.Contains("winws2", hint);
        Assert.Contains("sing-box", hint);
        Assert.Contains("не работа", hint);
        Assert.Contains("обхода нет", hint);
        Assert.DoesNotContain("не полностью", hint);

        var faulted = StatusView.BrokenHint([new ServiceState { Name = "sing-box", Health = ServiceHealth.Faulted }]);
        Assert.Contains("sing-box", faulted);
        Assert.Contains("супервизор перестал его поднимать", faulted);

        var view = new StatusView();
        view.Present(HomeState.Failed, hint, HomeState.Failed, HomeState.Failed);
        Assert.Equal(hint, ((TextBlock)view.FindName("StateHint")).Text);
    });

    // 4
    [Theory]
    [InlineData("Движок sing-box не работает")]
    [InlineData("winws2 не запущен")]
    [InlineData("Туннель не поднят")]
    public void Negations_are_not_painted_green(string message) =>
        Assert.Equal("Danger", StatusView.Tone(message));

    [Fact]
    public void Plain_success_stays_green() => Assert.Equal("Success", StatusView.Tone("Движок sing-box запущен"));

    // 5
    [Fact]
    public void Tray_badge_key_follows_theme_colors() => Sta.Run(() =>
    {
        var resources = Application.Current.Resources;
        var before = TrayIcon.BadgeKey(FieldMode.Running);

        resources["Success"] = new SolidColorBrush(Color.FromRgb(1, 2, 3));

        try
        {
            Assert.NotEqual(before, TrayIcon.BadgeKey(FieldMode.Running));
        }
        finally
        {
            resources.Remove("Success");
        }

        Assert.Equal(before, TrayIcon.BadgeKey(FieldMode.Running));
    });

    // 6
    [Fact]
    public void Side_menu_marks_the_section_itself_and_shows_section_states() => Sta.Run(() =>
    {
        var window = new MainWindow { ShowActivated = false };
        window.Show();
        Pump();

        try
        {
            // С боковым меню выбор отмечает сам пункт, черта прежней полосы скрыта.
            var bar = (Rectangle)window.FindName("NavIndicator");
            Assert.False(bar.IsVisible, "черта верхней полосы видна при боковом меню");

            var home = (RadioButton)window.FindName("RailStatus");
            Assert.True(home.IsChecked);
            Assert.Equal("Обзор", home.Content);

            // У ежедневных разделов справа — их состояние.
            var vpn = (RadioButton)window.FindName("SideVpn");
            Assert.Contains(SideValue.GetText(vpn), new[] { "Вкл", "Выкл" });

            var power = (TextBlock)window.FindName("PowerLabel");
            Assert.Contains(power.Text, new[] { "Включить обход", "Выключить обход" });
        }
        finally
        {
            Close(window);
        }
    });

    // 7
    [Fact]
    public void ParticleField_ignores_non_finite_sizes() => Sta.Run(() =>
    {
        var field = new ParticleField(5);
        var resize = typeof(ParticleField).GetMethod("Resize", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var w = typeof(ParticleField).GetField("_w", BindingFlags.Instance | BindingFlags.NonPublic)!;

        resize.Invoke(field, [double.NaN, double.PositiveInfinity]);
        Assert.True(double.IsFinite((double)w.GetValue(field)!));

        var lines = (int)typeof(ParticleField).GetField("MaxLines", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Assert.True(lines <= 150, $"MaxLines = {lines}: бюджет кадра программной отрисовки");
    });
}
