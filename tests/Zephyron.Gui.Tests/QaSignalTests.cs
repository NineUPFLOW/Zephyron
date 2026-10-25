using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;
using Zephyron.Core;
using Zephyron.Core.Themes;
using Zephyron.Gui.Views;

namespace Zephyron.Gui.Tests;

/// <summary>QA «Сигнала»: режимы движения, профили, утечки разделов, производные кисти тем, StatusLook, устойчивость слоя частиц.</summary>
[Collection(LeaksFirst.Name)]
public sealed class QaSignalTests
{
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Reset() => Motion.Use(new AppSettings());

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T hit)
                yield return hit;

            foreach (var inner in Descendants<T>(child))
                yield return inner;
        }
    }

    private static int ChangedSubscribers()
    {
        var field = typeof(Motion).GetField("Changed", BindingFlags.Static | BindingFlags.NonPublic)!;
        return ((Delegate?)field.GetValue(null))?.GetInvocationList().Length ?? 0;
    }

    private static int RunningClocks(FlowLanes lanes)
    {
        int n = 0;

        foreach (var name in new[] { "_desync", "_direct", "_tunnel" })
        {
            var lane = typeof(FlowLanes).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lanes)!;

            if (lane.GetType().GetField("Clock")!.GetValue(lane) is not null)
                n++;
        }

        return n;
    }

    // ---- а) Animations=false: бесконечные циклы стоят ---------------------------

    [Fact]
    public void Animations_off_stops_every_endless_loop() => Sta.Run(() =>
    {
        try
        {
            Motion.Use(new AppSettings { Animations = true, Effects = EffectsProfile.Full });
            // Анимации Windows подставляет Motion.Use: на раннере сборки они выключены.

            var field = new ParticleField(1) { ActiveOverride = true };
            var lanes = new FlowLanes { Width = 900 };
            var bar = new ProgressBar { IsIndeterminate = true };
            var panel = new StackPanel { Width = 900, Height = 400 };
            panel.Children.Add(field);
            panel.Children.Add(lanes);
            panel.Children.Add(bar);
            var window = new Window { Content = panel, Width = 900, Height = 500, ShowActivated = false };
            window.Show();
            Pump();

            try
            {
                lanes.Show(HomeState.Running, HomeState.Running, HomeState.Running);
                Pump();

                // Контроль: при включённом движении всё бежит, иначе тест ничего не доказывает.
                Assert.True(field.IsRunning, "ParticleField должен идти при Animations=true");
                Assert.Equal(3, RunningClocks(lanes));
                Assert.False(Motion.GetStill(bar));

                Motion.Use(new AppSettings { Animations = false, Effects = EffectsProfile.Full });
                Pump();
                Pump();

                Assert.False(Motion.Enabled);
                Assert.False(field.IsRunning, "ParticleField подписан на Rendering при Animations=false");
                Assert.Equal(0, field.ActiveNodes);
                Assert.Equal(0, RunningClocks(lanes));
                Assert.True(Motion.GetStill(bar));
                Assert.True(Motion.GetStill(window));

                // Новый показ состояния при выключенных анимациях тоже не заводит часы.
                lanes.Show(HomeState.Degraded, HomeState.Degraded, HomeState.Running);
                Pump();
                Assert.Equal(0, RunningClocks(lanes));

                // Возврат включает всё обратно.
                Motion.Use(new AppSettings { Animations = true, Effects = EffectsProfile.Full });
                Pump();
                Pump();
                Assert.True(field.IsRunning);
                Assert.Equal(3, RunningClocks(lanes));
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Reset();
        }
    });

    [Fact]
    public void Animations_off_stops_loops_in_the_real_window() => Sta.Run(() =>
    {
        try
        {
            Motion.Use(new AppSettings { Animations = true, Effects = EffectsProfile.Full });
            var window = new MainWindow { ShowActivated = false };
            var field = (ParticleField)window.FindName("Field");
            field.ActiveOverride = true;
            window.Show();
            Pump();

            try
            {
                Motion.Use(new AppSettings { Animations = false, Effects = EffectsProfile.Full });
                Pump();
                Pump();

                Assert.False(field.IsRunning);

                foreach (var lanes in Descendants<FlowLanes>(window))
                    Assert.Equal(0, RunningClocks(lanes));
            }
            finally
            {
                App.Exiting = true;
                window.Close();
                App.Exiting = false;
            }
        }
        finally
        {
            Reset();
        }
    });

    // ---- б) Full -> Lite -> Full ---------------------------------------------------

    [Fact]
    public void Profile_cycle_Full_Lite_Full_restores_nodes_subscription_and_glow() => Sta.Run(() =>
    {
        try
        {
            Motion.Use(new AppSettings { Effects = EffectsProfile.Full });
            int baseline = ChangedSubscribers();

            var field = new ParticleField(7) { ActiveOverride = true, Width = 400, Height = 200 };
            var glowing = new Button { Content = "x", Width = 80, Height = 30 };
            Glow.SetRadius(glowing, 10);
            Glow.SetStrength(glowing, .28);
            var panel = new StackPanel();
            panel.Children.Add(field);
            panel.Children.Add(glowing);
            var window = new Window { Content = panel, Width = 400, Height = 300, ShowActivated = false };
            window.Show();
            Pump();

            try
            {
                Assert.Equal(110, field.ActiveNodes);
                Assert.True(field.IsRunning);
                Assert.Equal(10, Assert.IsType<DropShadowEffect>(glowing.Effect).BlurRadius);
                int withField = ChangedSubscribers();

                Motion.Use(new AppSettings { Effects = EffectsProfile.Balanced });
                Assert.Equal(50, field.ActiveNodes);
                Assert.True(field.IsRunning);
                Assert.Equal(10, Assert.IsType<DropShadowEffect>(glowing.Effect).BlurRadius);

                Motion.Use(new AppSettings { Effects = EffectsProfile.Lite });
                Assert.Equal(0, field.ActiveNodes);
                Assert.False(field.IsRunning);
                Assert.Null(glowing.Effect);

                Motion.Use(new AppSettings { Effects = EffectsProfile.Full });
                Assert.Equal(110, field.ActiveNodes);
                Assert.True(field.IsRunning);
                var back = Assert.IsType<DropShadowEffect>(glowing.Effect);
                Assert.Equal(10, back.BlurRadius);
                Assert.Equal(.28, back.Opacity, 3);

                // Подписка одна: десять переключений не копят обработчики.
                for (int i = 0; i < 10; i++)
                {
                    Motion.Use(new AppSettings { Effects = EffectsProfile.Lite });
                    Motion.Use(new AppSettings { Effects = EffectsProfile.Full });
                }

                Assert.Equal(withField, ChangedSubscribers());
                Assert.Equal(110, field.ActiveNodes);
            }
            finally
            {
                window.Close();
                Pump();
            }

            Assert.Equal(baseline, ChangedSubscribers());
        }
        finally
        {
            Reset();
        }
    });

    [Fact]
    public void Primary_button_glow_follows_the_profile() => Sta.Run(() =>
    {
        try
        {
            Motion.Use(new AppSettings { Effects = EffectsProfile.Full });
            var button = new Button { Content = "Запустить", Style = (Style)Application.Current.FindResource("Primary") };
            var window = new Window { Content = button, ShowActivated = false };
            window.Show();
            Pump();

            try
            {
                Assert.True(Assert.IsType<DropShadowEffect>(button.Effect).BlurRadius > 0);
                Motion.Use(new AppSettings { Effects = EffectsProfile.Lite });
                Assert.Null(button.Effect);
                Motion.Use(new AppSettings { Effects = EffectsProfile.Full });
                Assert.True(Assert.IsType<DropShadowEffect>(button.Effect).BlurRadius > 0);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Reset();
        }
    });

    // ---- в) утечки разделов -----------------------------------------------------------

    private static void Open(MainWindow window, string tag)
    {
        var item = Descendants<RadioButton>(window).First(r => r.Tag as string == tag);
        item.IsChecked = true;
        Pump();
        Thread.Sleep(30);
        Pump();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (MainWindow Window, List<WeakReference> Status, List<WeakReference> Vpn) Cycle(int n, bool animations)
    {
        Motion.Use(new AppSettings { Animations = animations, Effects = EffectsProfile.Full });
        var window = new MainWindow { ShowActivated = false };
        window.Show();
        Pump();

        var status = new List<WeakReference>();
        var vpn = new List<WeakReference>();
        var host = (ContentControl)window.FindName("Section");

        for (int i = 0; i < n; i++)
        {
            Open(window, "vpn");
            vpn.Add(new WeakReference(host.Content));
            Assert.IsType<VpnView>(host.Content);

            Open(window, "status");
            status.Add(new WeakReference(host.Content));
            Assert.IsType<StatusView>(host.Content);
        }

        // Уходим на третий раздел: ни Status, ни Vpn больше не показаны.
        Open(window, "more");
        return (window, status, vpn);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Closed_sections_are_collected(bool animations) => Sta.Run(() =>
    {
        try
        {
            var (window, status, vpn) = Cycle(6, animations);

            try
            {
                for (int i = 0; i < 6; i++)
                {
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                    GC.WaitForPendingFinalizers();
                    Pump();
                }

                Assert.True(vpn.Count(r => r.IsAlive) == 0, $"VpnView удержано: {vpn.Count(r => r.IsAlive)} из {vpn.Count}");
                Assert.True(status.Count(r => r.IsAlive) == 0, $"StatusView удержано: {status.Count(r => r.IsAlive)} из {status.Count}");
            }
            finally
            {
                App.Exiting = true;
                window.Close();
                App.Exiting = false;
            }
        }
        finally
        {
            Reset();
        }
    });

    // ---- г, д) встроенные темы ------------------------------------------------------------

    private static string ThemesRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Zephyron.sln")) && Directory.Exists(Path.Combine(directory.FullName, "themes")))
                return Path.Combine(directory.FullName, "themes");

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("themes");
    }

    private static IEnumerable<string> AllThemeIds() =>
        Directory.GetDirectories(ThemesRoot()).Where(d => File.Exists(Path.Combine(d, "theme.json"))).Select(d => Path.GetFileName(d)!);

    private static ThemeColor Mix(ThemeColor c) => c;

    [Fact]
    public void Every_shipped_theme_builds_with_visible_derived_brushes() => Sta.Run(() =>
    {
        var root = ThemesRoot();
        var ids = AllThemeIds().ToList();
        Assert.True(ids.Count >= 14, "найдено тем: " + ids.Count);
        var bad = new List<string>();

        foreach (var id in ids)
        {
            var load = ThemeLoader.Load(id, root);

            if (load.Theme is null)
            {
                bad.Add($"{id}: не загрузилась: {string.Join("; ", load.Problems)}");
                continue;
            }

            var problems = new List<string>();
            ResourceDictionary built;

            try
            {
                built = Zephyron.Gui.Themes.Build(load.Theme, problems);
            }
            catch (Exception ex)
            {
                bad.Add($"{id}: Build бросил {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            if (problems.Count > 0)
                bad.Add($"{id}: Build: {string.Join("; ", problems)}");

            foreach (var key in new[] { "FieldLine", "Scrim", "Accent2", "CardRim", "AccentGradient" })
            {
                if (built[key] is not Brush brush)
                {
                    bad.Add($"{id}: нет кисти {key}");
                    continue;
                }

                bool visible = brush switch
                {
                    SolidColorBrush s => s.Color.A > 0 && s.Opacity > 0,
                    GradientBrush g => g.GradientStops.Count >= 2 && g.GradientStops.All(s => s.Color.A > 0) && g.Opacity > 0,
                    _ => false,
                };

                if (!visible)
                    bad.Add($"{id}: кисть {key} прозрачна или неизвестного вида ({brush.GetType().Name})");

                if (!brush.IsFrozen)
                    bad.Add($"{id}: кисть {key} не заморожена");
            }
        }

        Assert.True(bad.Count == 0, string.Join(Environment.NewLine, bad));
    });

    private static double HueGap(double a, double b)
    {
        double d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    [Fact]
    public void Accent2_hue_differs_from_Success_by_30_degrees_in_every_theme() => Sta.Run(() =>
    {
        var root = ThemesRoot();
        var report = new List<string>();
        var bad = new List<string>();

        foreach (var id in AllThemeIds())
        {
            var load = ThemeLoader.Load(id, root);

            if (load.Theme is null)
            {
                bad.Add($"{id}: не загрузилась");
                continue;
            }

            var built = Zephyron.Gui.Themes.Build(load.Theme, []);
            var a2 = (Color)built["Accent2Color"];
            var ok = (Color)built["SuccessColor"];
            var (h2, s2, _) = ColorMath.ToHsl(new ThemeColor(255, a2.R, a2.G, a2.B));
            var (hs, _, _) = ColorMath.ToHsl(new ThemeColor(255, ok.R, ok.G, ok.B));
            double gap = HueGap(h2, hs);
            report.Add($"{id}: Accent2 {a2} h={h2:F0} s={s2:F2}, Success {ok} h={hs:F0}, разница {gap:F0}");

            if (gap < 30)
                bad.Add($"{id}: Accent2 {a2} (h={h2:F0}) и Success {ok} (h={hs:F0}) — разница {gap:F0} < 30");
        }

        Assert.True(bad.Count == 0, string.Join(Environment.NewLine, bad) + Environment.NewLine + "--- все:" + Environment.NewLine + string.Join(Environment.NewLine, report));
    });

    // ---- е) StatusLook -----------------------------------------------------------------------

    private static bool LooksFailed(string text)
    {
        var box = new Border();
        var status = new TextBlock { Visibility = Visibility.Collapsed };
        var mark = new Control();
        StatusLook.Attach(box, status, mark);
        status.Visibility = Visibility.Visible;
        status.Text = text;
        return mark.Visibility == Visibility.Visible && (mark.Tag as string) == "Failed";
    }

    /// <summary>Фразы дословно из кода вью (файл:строка); второй столбец — это ошибка по смыслу.</summary>
    public static TheoryData<string, bool, string> Phrases => new()
    {
        { "Не удалось записать выбор: x", true, "DesyncView.xaml.cs:176" },
        { "Пресеты не читаются: x", true, "DesyncView.xaml.cs:225" },
        { "Модуль m подключить не вышло: x", true, "DesyncView.xaml.cs:563" },
        { "Не вышло: x", true, "DesyncView.xaml.cs:746" },
        { "Не удалось открыть папку: x", true, "DesyncView.xaml.cs:767" },
        { "Файл пресета «p» не найден в C:\\z.", true, "DesyncView.xaml.cs:804" },
        { "Проверка не удалась: x", true, "DnsView.xaml.cs:648" },
        { "Cloudflare не ответил ничем: UDP — таймаут", true, "DnsView.xaml.cs:612" },
        { "Автовыбор: ни один резолвер для туннеля не ответил по DoH — выбор не менялся.", true, "DnsView.xaml.cs:736" },
        { "Каталог адресов Zapret не найден — остаётся честный резолвер.", true, "PinWindow.xaml.cs:432" },
        { "Не удалось закрепить: x", true, "PinWindow.xaml.cs:503" },
        { "Пины напрямую не прочитались: x", true, "RoutesSettingsWindow.xaml.cs:53" },
        { "Сервис: не записалось — x", true, "RoutesSettingsWindow.xaml.cs:111" },
        { "Правила не читаются: x", true, "RoutesView.xaml.cs:740" },
        { "Не удалось сохранить порядок: x", true, "RoutesView.xaml.cs:1043" },
        { "Не удалось записать: x", true, "RoutesView.xaml.cs:2472" },
        { "Не скопировалось: x", true, "TgProxyView.xaml.cs:446" },
        { "Telegram не открыл ссылку — скопируйте её и нажмите в «Избранном».", true, "TgProxyView.xaml.cs:423" },
        { "Не удалось очистить: x", true, "TunnelSettingsWindow.xaml.cs:247" },

        // Не ошибки: информация, ход работы, успех.
        { "Пресетов: 4, из них наших 2.", false, "DesyncView.xaml.cs:218" },
        { "Выбран «p». Применится при следующем запуске движков.", false, "DesyncView.xaml.cs:975" },
        { "Открыт p.bat. Правка подействует после перезапуска движков.", false, "DesyncView.xaml.cs:722" },
        { "Пресет не выбран — открывать нечего.", false, "DesyncView.xaml.cs:796" },
        { "Задержку покажет обзор выше.", false, "DnsView.xaml.cs:293" },
        { "Автовыбор включён — замеряю резолверы…", false, "DnsView.xaml.cs:705" },
        { "Автовыбор: быстрейший и так стоит — Cloudflare.", false, "DnsView.xaml.cs:742" },
        { "Апстрим: 1.1.1.1. Применится при следующем запуске движков.", false, "DnsView.xaml.cs:563" },
        { "Проверяю Cloudflare: UDP, DoT и DoH…", false, "DnsView.xaml.cs:601" },
        { "Спрашиваю адреса и проверяю каждый…", false, "PinWindow.xaml.cs:456" },
        { "Спрошено 3 из 9…", false, "PinWindow.xaml.cs:856" },
        { "Снято имён: 2. Осталось прибитых: 0.", false, "PinWindow.xaml.cs:880" },
        { "Порядок сохранён: a встал на место b.", false, "RoutesView.xaml.cs:1038" },
        { "Правила для x.com уже нет — перечитываю раздел.", false, "RoutesView.xaml.cs:2186" },
        { "Впишите сайт (example.com), адрес или подсеть в поле поиска — если такого нет, появится «Добавить».", false, "RoutesView.xaml.cs:2395" },
        { "Добавлена «p»: домены и адреса пустые и выключены.", false, "RoutesView.xaml.cs:2467" },
        { "Записано: x.com → напрямую.", false, "RoutesView.xaml.cs:2652" },
        { "Ссылка отправлена Telegram — подтвердите подключение в нём.", false, "TgProxyView.xaml.cs:419" },
        { "Введите адрес и нажмите «Сохранить».", false, "TunnelSettingsWindow.xaml.cs:369" },
        { "Память замеров очищена: задержки появятся со следующим замером.", false, "TunnelSettingsWindow.xaml.cs:242" },
    };

    [Theory]
    [MemberData(nameof(Phrases))]
    public void StatusLook_classifies_real_status_lines(string text, bool isError, string where) => Sta.Run(() =>
    {
        Assert.True(LooksFailed(text) == isError, $"{where}: «{text}» — ожидалось {(isError ? "ошибка" : "не ошибка")}");
    });

    [Fact]
    public void StatusLook_empty_and_hidden_text_hides_the_box() => Sta.Run(() =>
    {
        var box = new Border();
        var status = new TextBlock();
        var mark = new Control();
        StatusLook.Attach(box, status, mark);
        Assert.Equal(Visibility.Collapsed, box.Visibility);

        status.Text = "Пока пусто";
        status.Visibility = Visibility.Visible;
        Assert.Equal(Visibility.Visible, box.Visibility);
        Assert.Equal("Off", mark.Tag);

        status.Text = string.Empty;
        Assert.Equal(Visibility.Collapsed, box.Visibility);

        status.Text = "Не удалось: x";
        status.Visibility = Visibility.Collapsed;
        Assert.Equal(Visibility.Collapsed, box.Visibility);
        Assert.Equal(Visibility.Collapsed, mark.Visibility);
    });

    // ---- ж) ParticleField на мусорных числах ----------------------------------------------------

    private static readonly double[] Hostile =
        [double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1, -1e9, 0, double.MaxValue, double.Epsilon];

    private static void Frames(ParticleField field)
    {
        var step = typeof(ParticleField).GetMethod("Step", BindingFlags.Instance | BindingFlags.NonPublic)!;

        for (int i = 0; i < 5; i++)
            step.Invoke(field, [0.05, 1.0, Motion.Profile]);

        var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(field);
    }

    [Fact]
    public void ParticleField_survives_hostile_pointer_burst_and_resize() => Sta.Run(() =>
    {
        try
        {
            Motion.Use(new AppSettings { Effects = EffectsProfile.Full });
            var field = new ParticleField(3) { ActiveOverride = true, Width = 400, Height = 300 };
            var window = new Window { Content = field, Width = 400, Height = 300, ShowActivated = false };
            window.Show();
            Pump();

            try
            {
                var resize = typeof(ParticleField).GetMethod("Resize", BindingFlags.Instance | BindingFlags.NonPublic)!;

                foreach (var x in Hostile)
                {
                    foreach (var y in Hostile)
                    {
                        var ex = Record.Exception(() =>
                        {
                            field.Pointer(new Point(x, y));
                            field.Burst(new Point(x, y));
                            Frames(field);
                            field.Leave();
                            Frames(field);
                            field.Mode = FieldMode.Failed;
                            field.Mode = FieldMode.Running;
                            field.Mbps = x;
                            Frames(field);
                            resize.Invoke(field, [x, y]);
                            Frames(field);
                            resize.Invoke(field, [400.0, 300.0]);
                        });

                        Assert.True(ex is null, $"({x}; {y}): {ex}");
                    }
                }

                field.Dim = double.NaN;
                field.Dim = double.PositiveInfinity;
                field.Dim = -5;
                Frames(field);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Reset();
        }
    });

    [Fact]
    public void ParticleField_nodes_are_finite_after_a_hostile_resize_is_followed_by_a_normal_one() => Sta.Run(() =>
    {
        try
        {
            Motion.Use(new AppSettings { Effects = EffectsProfile.Full });
            var field = new ParticleField(3) { ActiveOverride = true, Width = 400, Height = 300 };
            var window = new Window { Content = field, Width = 400, Height = 300, ShowActivated = false };
            window.Show();
            Pump();

            try
            {
                var resize = typeof(ParticleField).GetMethod("Resize", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var xs = (double[])typeof(ParticleField).GetField("_x", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(field)!;
                var ys = (double[])typeof(ParticleField).GetField("_y", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(field)!;

                foreach (var bad in new[] { double.NaN, double.PositiveInfinity, -50 })
                {
                    resize.Invoke(field, [bad, bad]);
                    resize.Invoke(field, [400.0, 300.0]);

                    for (int i = 0; i < field.ActiveNodes; i++)
                        Assert.True(double.IsFinite(xs[i]) && double.IsFinite(ys[i]) && xs[i] >= 0 && ys[i] >= 0,
                            $"после Resize({bad}) узел {i} = ({xs[i]}; {ys[i]})");
                }
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Reset();
        }
    });
    // ---- локализация утечки: какие разделы остаются жить после выгрузки --------------------

    public static TheoryData<string, Func<UserControl>> LeakViews => new()
    {
        { "Состояние", () => new StatusView() },
        { "VPN", () => new VpnView() },
        { "Десинк", () => new DesyncView() },
        { "DNS", () => new DnsView() },
        { "Файл hosts", () => new HostsView() },
        { "TG Proxy", () => new TgProxyView() },
        { "Проверка", () => new CheckView() },
        { "Замер скорости", () => new SpeedView() },
        { "Диагностика", () => new DoctorView() },
        { "Наблюдение", () => new WatchView() },
        { "Оформление", () => new AppearanceView() },
        { "Прочее", () => new MoreView() },
    };

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference ShowAndDrop(Func<UserControl> make)
    {
        var view = make();
        var window = new Window { Content = view, Width = 900, Height = 600, ShowActivated = false };
        window.Show();
        Pump();
        window.Content = null;
        Pump();
        window.Close();
        Pump();
        return new WeakReference(view);
    }

    [Theory]
    [MemberData(nameof(LeakViews))]
    public void A_view_removed_from_a_window_is_collected(string name, Func<UserControl> make) => Sta.Run(() =>
    {
        try
        {
            Motion.Use(new AppSettings { Effects = EffectsProfile.Full });
            var weak = ShowAndDrop(make);

            // До 3 с с паузами, как в MainWindowShellTests: фоновые запросы раздела на
            // загрузке держат его, пока не вернутся; подписку или таймер ловим всё равно.
            var clock = System.Diagnostics.Stopwatch.StartNew();

            while (weak.IsAlive && clock.Elapsed < TimeSpan.FromSeconds(3))
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                GC.WaitForPendingFinalizers();
                Pump();
                Thread.Sleep(50);
                Pump();
            }

            Assert.False(weak.IsAlive, $"{name}: раздел удержан после закрытия окна");
        }
        finally
        {
            Reset();
        }
    });

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference AttachAndDrop()
    {
        var text = new TextBlock();
        StatusLook.Attach(new Border(), text, new Control());
        return new WeakReference(text);
    }

    [Fact]
    public void StatusLook_Attach_does_not_pin_the_text_block() => Sta.Run(() =>
    {
        var weak = AttachAndDrop();

        for (int i = 0; i < 4; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }

        Assert.False(weak.IsAlive, "DependencyPropertyDescriptor.AddValueChanged держит TextBlock (и всё дерево раздела) в статическом кэше");
    });
}
