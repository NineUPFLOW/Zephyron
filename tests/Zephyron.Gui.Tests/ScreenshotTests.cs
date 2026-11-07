using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace Zephyron.Gui.Tests;

/// <summary>
/// Снимки разделов окна в PNG — чтобы посмотреть на оформление без прав
/// администратора, которых требует манифест.
/// </summary>
/// <remarks>
/// Молчит, пока не задана переменная <c>ZEPHYRON_SHOTS</c> — папка для снимков:
/// в CI и при обычном прогоне это пустой тест. Снимок — содержимое окна без
/// рамки Windows, с теми данными, что есть на этой машине. Запускать отдельно:
/// <c>dotnet test gui\Zephyron.Gui.sln --filter ScreenshotTests</c> — среди
/// прочих тестов окну не хватает минуты, отведённой потоку STA.
/// </remarks>
public sealed class ScreenshotTests
{
    private static readonly string[] Sections =
        ["vpn", "status", "desync", "routes", "tgproxy", "check", "speed", "dns", "hosts", "watch", "doctor", "look", "more"];

    /// <summary>Обычный размер окна и нижний предел — одним окном: закрытое гасит тестовое приложение.</summary>
    private static readonly (int Width, int Height)[] Sizes = [(1160, 760), (880, 560)];

    [Fact]
    public void Sections_are_rendered_to_png()
    {
        string? folder = Environment.GetEnvironmentVariable("ZEPHYRON_SHOTS");

        if (string.IsNullOrEmpty(folder))
            return;

        Directory.CreateDirectory(folder);

        Sta.Run(() =>
        {
            var window = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                Top = 0,
                ShowActivated = false,
                ShowInTaskbar = false,
            };

            window.Show();

            foreach (var (width, height) in Sizes)
            {
                window.Width = width;
                window.Height = height;
                Pump(TimeSpan.FromMilliseconds(800));

                // На свежей машине «Главная» сперва показывает мастер первого запуска.
                Save((FrameworkElement)window.Content, Path.Combine(folder, $"{width}x{height}-start.png"));

                foreach (string section in Sections)
                {
                    window.Open(section);
                    Pump(TimeSpan.FromMilliseconds(700));
                    Save((FrameworkElement)window.Content, Path.Combine(folder, $"{width}x{height}-{section}.png"));
                    SaveFull(window, Path.Combine(folder, $"{width}x{height}-{section}-full.png"));
                }
            }

            // Светлая палитра поверх той же разметки: подмена словаря, как при смене темы.
            var palettes = Application.Current.Resources.MergedDictionaries;
            int dark = palettes.ToList().FindIndex(d => d.Source?.OriginalString.EndsWith("Palette.xaml") == true);

            if (dark >= 0)
            {
                var darkPalette = palettes[dark];
                palettes[dark] = new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/Zephyron;component/Theme/Light.xaml", UriKind.Absolute),
                };

                foreach (string section in new[] { "vpn", "status", "dns", "hosts" })
                {
                    window.Open(section);
                    Pump(TimeSpan.FromMilliseconds(700));
                    Save((FrameworkElement)window.Content, Path.Combine(folder, $"light-{section}.png"));
                    SaveFull(window, Path.Combine(folder, $"light-{section}-full.png"));
                }

                palettes[dark] = darkPalette;
            }

            // Состояния «Главной»: движков здесь не поднять, поэтому состояние задаёт
            // внутренний Present только этим снимкам — само окно показывает лишь настоящее.
            var states = new (string Name, Views.HomeState State, string Hint, Views.HomeState Desync, Views.HomeState Tunnel)[]
            {
                ("running", Views.HomeState.Running, "Трафик идёт через десинк.", Views.HomeState.Running, Views.HomeState.Off),
                ("starting", Views.HomeState.Starting, "Движки поднимаются — 4 с", Views.HomeState.Starting, Views.HomeState.Starting),
                ("degraded", Views.HomeState.Degraded, "Часть движков не в порядке — подробности ниже.", Views.HomeState.Running, Views.HomeState.Degraded),
                ("failed", Views.HomeState.Failed, Views.StatusView.BrokenHint([new Zephyron.Supervisor.ServiceState { Name = "winws2", Health = Zephyron.Supervisor.ServiceHealth.Dead }]), Views.HomeState.Failed, Views.HomeState.Off),
            };

            foreach (var (width, height) in Sizes)
            {
                window.Width = width;
                window.Height = height;
                window.Open("vpn");
                window.Open("status");
                Pump(TimeSpan.FromMilliseconds(700));

                if (window.Section.Content is not Views.StatusView home)
                    continue;

                foreach (var (name, state, hint, desync, tunnel) in states)
                {
                    home.Present(state, hint, desync, tunnel);
                    home.Lanes.Flow = 24;

                    if (state == Views.HomeState.Running)
                    {
                        // Графики с замерами — тоже только для снимка: кривая из синуса.
                        var down = Enumerable.Range(0, 60).Select(i => 18 + 12 * Math.Sin(i / 6.0)).ToArray();
                        var up = Enumerable.Range(0, 60).Select(i => 3 + 2 * Math.Cos(i / 5.0)).ToArray();
                        var ping = Enumerable.Range(0, 24).Select(i => i == 9 ? double.NaN : 32 + 9 * Math.Sin(i / 3.0)).ToArray();
                        home.ThroughputChart.Show([new(down, (Brush)home.FindResource("Accent2"), true), new(up, (Brush)home.FindResource("Text"), false, Dashed: true, Opacity: 0.75)]);
                        home.LatencyChart.Show([new(ping, (Brush)home.FindResource("Accent2"), true)], marks: [9]);
                    }

                    Pump(TimeSpan.FromMilliseconds(900));
                    Save((FrameworkElement)window.Content, Path.Combine(folder, $"{width}x{height}-status-{name}.png"));
                    SaveFull(window, Path.Combine(folder, $"{width}x{height}-status-{name}-full.png"));
                }

                home.PageScroller.ScrollToVerticalOffset(380);
                Pump(TimeSpan.FromMilliseconds(400));
                Save((FrameworkElement)window.Content, Path.Combine(folder, $"{width}x{height}-status-lower.png"));
            }

            window.Width = 1160;
            window.Height = 760;
            window.RestartOnboarding();
            Pump(TimeSpan.FromMilliseconds(700));
            Save((FrameworkElement)window.Content, Path.Combine(folder, "onboarding.png"));

            // Окна-диалоги: у них своя разметка поверх тех же стилей.
            var dialogs = new (string Name, Func<Window> Make)[]
            {
                ("tunnel-settings", () => new Views.TunnelSettingsWindow()),
                ("routes-settings", () => new Views.RoutesSettingsWindow()),
                ("subscription-add", () => new Views.SubscriptionAddWindow([])),
                ("folder-name", () => new Views.FolderNameWindow("Новая папка", [])),
                ("program-picker", () => new Views.ProgramPickerWindow()),
                ("subscription-edit", () => new Views.SubscriptionEditWindow("Моя подписка", [])),
                ("list-name", () => new Views.ListNameWindow("example.com", [])),
                ("path", () => new Views.PathWindow("YouTube", Views.PathPick.Default, null, "напрямую", true, "рецепт по умолчанию", null)),
                ("route-list", () => new Views.RouteListWindow("7tv.app\ncdn.7tv.app", engine: null, [], folder: null, reserved: [])),
                ("color-picker", () => new Views.ColorPickerWindow(new Zephyron.Core.Themes.ThemeColor(255, 155, 123, 255), "Акцент")),
                ("update", () => new Views.UpdateWindow(new Zephyron.Core.Updates.ReleaseInfo
                {
                    Version = "9.9.9",
                    Tag = "v9.9.9",
                    ArchiveUrl = "https://example/Zephyron-9.9.9.zip",
                    ArchiveSize = 1_000_000,
                    Notes = "# Что нового\n\n## Главная\n\n### Новое:\n\n**Неон.** Новое оформление.",
                })),
                ("log", () => new Views.LogWindow()),
            };

            // Меню значка в трее — своё окно без рамки.
            var tray = new TrayMenu(() => { }, _ => { }, () => { }, _ => { });
            tray.Render(new TrayStatus(Running: true, AllHealthy: true, Tunnel: Zephyron.Supervisor.ServiceHealth.Healthy,
                Desync: Zephyron.Supervisor.ServiceHealth.Healthy, Busy: false));
            tray.WindowStartupLocation = WindowStartupLocation.Manual;
            tray.Left = -20000;
            tray.Show();
            Pump(TimeSpan.FromMilliseconds(500));
            Save((FrameworkElement)tray.Content, Path.Combine(folder, "tray-menu.png"), (Brush)Application.Current.FindResource("Backdrop"));
            tray.Close();

            foreach (var (name, make) in dialogs)
            {
                var dialog = make();
                dialog.WindowStartupLocation = WindowStartupLocation.Manual;
                dialog.Left = -20000;
                dialog.ShowActivated = false;
                dialog.Show();
                Pump(TimeSpan.FromMilliseconds(500));
                Save(Chrome(dialog), Path.Combine(folder, $"dialog-{name}.png"), dialog.Background);
                dialog.Close();
            }
        }, TimeSpan.FromMinutes(6));
    }

    private const string WarpFile = """
        [Interface]
        PrivateKey = yAnz5TF+lXXJte14tji3zlMNq+hd2rYUIgJBgB3fBmk=
        Address = 172.16.0.2/32
        DNS = 1.1.1.1
        [Peer]
        PublicKey = bmXOC+F1FxEMF9dyiK2H5/1SUtzH0JuVo51h2wPfgyo=
        Endpoint = 162.159.192.1:2408
        AllowedIPs = 0.0.0.0/0
        """;

    /// <summary>
    /// Состояния режима и пути туннеля (Десинк / Гибрид и Туннель с VPN или WARP, с файлом и без, с подписками и без) на вкладках
    /// VPN, «Главная», «Маршруты», «TG Proxy» и окно автоподбора. Настройки пишутся в config рядом с тестом.
    /// </summary>
    [Fact]
    public void Tunnel_path_states_are_rendered_to_png()
    {
        string? folder = Environment.GetEnvironmentVariable("ZEPHYRON_SHOTS");

        if (string.IsNullOrEmpty(folder))
            return;

        Directory.CreateDirectory(folder);
        Directory.CreateDirectory("config");

        // Правила и списки из репозитория — без них «Маршруты» не открываются; свои настройки не затираем.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var source = Path.Combine(dir.FullName, "config");

            if (!File.Exists(Path.Combine(source, "rules.yaml")))
                continue;

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine("config", Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                if (!File.Exists(target))
                    File.Copy(file, target);
            }

            break;
        }

        const string sub = "https://example.invalid/sub";
        var fresh = Zephyron.Core.AppSettings.Fresh;
        var withSub = fresh with { SubscriptionUrl = sub };

        Zephyron.Core.AppSettings Mode(Zephyron.Core.AppSettings s, Zephyron.Core.Rules.WorkMode mode, Zephyron.Core.Rules.TunnelPath path) =>
            Zephyron.Core.Rules.TunnelPaths.Apply(Zephyron.Core.Rules.WorkModes.Choose(s, mode) with { SubscriptionUrl = s.SubscriptionUrl }, path);

        var states = new (string Name, Func<Zephyron.Core.AppSettings> Settings, bool Book, bool File)[]
        {
            ("desync", () => Mode(withSub, Zephyron.Core.Rules.WorkMode.Desync, Zephyron.Core.Rules.TunnelPath.Vpn), true, false),
            ("hybrid-vpn", () => Mode(withSub, Zephyron.Core.Rules.WorkMode.Route, Zephyron.Core.Rules.TunnelPath.Vpn), true, false),
            ("hybrid-vpn-nosub", () => Mode(fresh, Zephyron.Core.Rules.WorkMode.Route, Zephyron.Core.Rules.TunnelPath.Vpn), false, false),
            ("hybrid-warp", () => Mode(withSub, Zephyron.Core.Rules.WorkMode.Route, Zephyron.Core.Rules.TunnelPath.Warp), true, false),
            ("hybrid-warp-file", () => Mode(withSub, Zephyron.Core.Rules.WorkMode.Route, Zephyron.Core.Rules.TunnelPath.Warp), true, true),
            ("tunnel-warp-nosub", () => Mode(fresh, Zephyron.Core.Rules.WorkMode.Tunnel, Zephyron.Core.Rules.TunnelPath.Warp), false, false),
            ("tunnel-vpn", () => Mode(withSub, Zephyron.Core.Rules.WorkMode.Tunnel, Zephyron.Core.Rules.TunnelPath.Vpn), true, false),
        };

        Sta.Run(() =>
        {
            var window = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                Top = 0,
                ShowActivated = false,
                ShowInTaskbar = false,
                Width = 1160,
                Height = 760,
            };

            window.Show();
            Pump(TimeSpan.FromMilliseconds(800));

            foreach (var (name, settings, book, file) in states)
            {
                settings().Save(Zephyron.Core.AppSettings.DefaultPath);

                var entries = new SubscriptionBook();

                if (book)
                    entries.Entries.Add(new SubscriptionEntry { Name = "Основная", Url = sub, InPool = true });

                entries.Save();

                if (file)
                {
                    File.WriteAllText("warp-shot.conf", WarpFile);
                    Zephyron.Subscriptions.Warp.SaveFile("warp-shot.conf");
                }
                else if (File.Exists(Zephyron.Subscriptions.Warp.FilePath))
                {
                    Zephyron.Subscriptions.Warp.RemoveFile();
                }

                foreach (string section in new[] { "vpn", "status", "routes", "tgproxy" })
                {
                    window.Open("status");
                    window.Open(section);
                    Pump(TimeSpan.FromMilliseconds(1200));
                    SaveFull(window, Path.Combine(folder, $"path-{name}-{section}.png"));
                }
            }

            // Минимальная ширина окна: вкладка VPN и «Главная» в двух путях туннеля.
            window.Width = 880;
            window.Height = 560;

            foreach (var (name, settings, book, file) in states.Where(x => x.Name is "hybrid-vpn" or "hybrid-warp"))
            {
                settings().Save(Zephyron.Core.AppSettings.DefaultPath);

                var entries = new SubscriptionBook();
                entries.Entries.Add(new SubscriptionEntry { Name = "Основная", Url = sub, InPool = true });
                entries.Save();

                foreach (string section in new[] { "vpn", "status" })
                {
                    window.Open("status");
                    window.Open(section);
                    Pump(TimeSpan.FromMilliseconds(1200));
                    SaveFull(window, Path.Combine(folder, $"min-{name}-{section}.png"));
                }
            }

            var autopick = new Views.AutoPickWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                ShowActivated = false,
            };

            autopick.Show();
            Pump(TimeSpan.FromMilliseconds(700));
            Save(Chrome(autopick), Path.Combine(folder, "dialog-autopick.png"), autopick.Background);
            autopick.Close();

            // Состояния были записаны в config рядом с тестом — убираем, чтобы не влиять на прочие тесты.
            foreach (var leftover in new[] { Zephyron.Core.AppSettings.DefaultPath, SubscriptionBook.DefaultPath,
                         Zephyron.Subscriptions.Warp.FilePath, Zephyron.Subscriptions.Warp.FileNamePath, "warp-shot.conf" })
            {
                File.Delete(leftover);
            }
        }, TimeSpan.FromMinutes(6));
    }

    /// <summary>
    /// Снимок страницы целиком: окно раздвигается на высоту содержимого,
    /// поэтому видно всё, что есть в разделе, а не только верхний экран.
    /// </summary>
    /// <remarks>
    /// Высота берётся у полосы прокрутки раздела (<see cref="ScrollViewer.ExtentHeight"/>),
    /// к ней добавляется всё, что вне окна прокрутки, — полоса меню и поля
    /// страницы. Предел 8000 точек: выше снимок становится нечитаемо длинным
    /// и занимает сотни мегабайт. Окно возвращается к прежнему размеру.
    /// </remarks>
    private static void SaveFull(Window window, string path)
    {
        if (window.Content is not FrameworkElement root)
            return;

        var scroller = FindScrollViewer(root);

        if (scroller is null || scroller.ScrollableHeight <= 0)
        {
            Save(root, path);
            return;
        }

        double chrome = window.Height - root.ActualHeight;
        double outside = root.ActualHeight - scroller.ViewportHeight;
        double needed = Math.Min(8000, Math.Ceiling(scroller.ExtentHeight + outside) + 4) + chrome;
        double old = window.Height;

        window.Height = Math.Max(needed, old);
        Pump(TimeSpan.FromMilliseconds(450));
        scroller.ScrollToTop();
        Pump(TimeSpan.FromMilliseconds(150));
        Save((FrameworkElement)window.Content, path);

        window.Height = old;
        Pump(TimeSpan.FromMilliseconds(200));
    }

    /// <summary>Первая полоса прокрутки с содержимым выше окна; иначе — любая.</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        ScrollViewer? first = null;

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is ScrollViewer viewer)
            {
                if (viewer.ScrollableHeight > 0)
                    return viewer;

                first ??= viewer;
            }

            var deeper = FindScrollViewer(child);

            if (deeper is not null)
                return deeper;
        }

        return first;
    }

    private static void Pump(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = time };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Корень шаблона окна: с его полями, которых у Content нет.</summary>
    private static FrameworkElement Chrome(Window window) => (FrameworkElement)VisualTreeHelper.GetChild(window, 0);

    /// <param name="back">Фон окна: он у окна, а не у содержимого, и без него диалог выходит на белом.</param>
    private static void Save(FrameworkElement root, string path, Brush? back = null)
    {
        var size = new Size(Math.Ceiling(root.ActualWidth), Math.Ceiling(root.ActualHeight));
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            if (back is not null)
                context.DrawRectangle(back, null, new Rect(size));

            // Окно кадра — сам элемент: иначе кисть растягивает его по границам выступающих детей.
            var brush = new VisualBrush(root) { Viewbox = new Rect(size), ViewboxUnits = BrushMappingMode.Absolute };
            context.DrawRectangle(brush, null, new Rect(size));
        }

        bitmap.Render(visual);

        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));

        using var file = File.Create(path);
        png.Save(file);
    }
}
