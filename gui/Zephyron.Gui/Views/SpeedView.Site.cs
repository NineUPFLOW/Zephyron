using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Zephyron.Core;
using Zephyron.Core.Rules;
using Zephyron.Proxy;
using Zephyron.Supervisor;

namespace Zephyron.Gui.Views;

/// <summary>
/// «Скорость до сайта» — карточка под основным замером: сама работа в <see cref="SiteSpeed"/>,
/// здесь только ввод, показ по мере прихода этапов и сравнение путей.
/// </summary>
/// <remarks>
/// Замер живёт, пока раздел открыт: он короткий (секунды) и трафика почти не тратит,
/// так что уход со страницы его просто обрывает.
/// </remarks>
public partial class SpeedView
{
    private CancellationTokenSource? _site;
    private string _siteText = string.Empty;
    private string? _siteIp;

    private void OnSiteText(object sender, TextChangedEventArgs e)
    {
        if (SiteGo is null)
            return;

        var uri = SiteSpeed.Normalize(SiteBox.Text);

        // Итоговый хост виден до замера: что именно будет измерено.
        SiteHost.Text = uri is null ? string.Empty : "Адрес: " + uri.Host;
        SiteGo.IsEnabled = uri is not null && _site is null;
        SiteCompare.IsEnabled = SiteGo.IsEnabled;
    }

    private void OnSiteKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SiteGo.IsEnabled)
        {
            e.Handled = true;
            OnSiteGo(sender, e);
        }
    }

    private async void OnSiteGo(object sender, RoutedEventArgs e)
    {
        if (_site is not null)
        {
            _site.Cancel();
            return;
        }

        if (SiteSpeed.Normalize(SiteBox.Text) is not { } uri)
        {
            ShowSiteError("Некорректный адрес");
            return;
        }

        await RunSiteAsync(compare: false, uri);
    }

    private async void OnSiteCompare(object sender, RoutedEventArgs e)
    {
        if (_site is null && SiteSpeed.Normalize(SiteBox.Text) is { } uri)
            await RunSiteAsync(compare: true, uri);
    }

    private async Task RunSiteAsync(bool compare, Uri uri)
    {
        using var run = new CancellationTokenSource();
        _site = run;

        SiteBox.IsEnabled = false;
        SiteGo.Content = "Остановить";
        SiteGo.IsEnabled = true;
        SiteCompare.IsEnabled = false;
        SiteError.Visibility = Visibility.Collapsed;
        SiteBlock.Visibility = Visibility.Collapsed;
        SiteEmpty.Visibility = Visibility.Collapsed;
        CompareCard.Visibility = Visibility.Collapsed;
        SiteResultGrid.Visibility = Visibility.Visible;
        SiteAgain.Visibility = SiteCopy.Visibility = Visibility.Collapsed;
        SiteStatus.Visibility = Visibility.Visible;
        SiteSummary.Text = string.Empty;

        ShowSite(new SiteResult { Host = uri.Host }, final: false);
        var viaTunnel = await ShowRouteAsync(uri.IdnHost);

        try
        {
            if (compare)
                await CompareAsync(uri, run.Token);
            else
            {
                // Тем путём, которым имя ведёт Zephyron, а не переключателем общего замера:
                // иначе сайт «на десинке» мерился через туннель — цифры чужого пути.
                var result = await MeasureAsync(uri, viaTunnel && _tunnelUp, run.Token);

                if (result is not null)
                    FinishSite(result);
            }
        }
        catch (OperationCanceledException)
        {
            SiteStatus.Text = "Остановлено.";
            SiteResultGrid.Visibility = Visibility.Collapsed;
            SiteEmpty.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ShowSiteError("Замер не получился: " + ex.GetBaseException().Message);
        }
        finally
        {
            _site = null;
            SiteBox.IsEnabled = true;
            SiteGo.Content = "Измерить";
            SiteStatus.Visibility = Visibility.Collapsed;
            OnSiteText(this, null!);
        }
    }

    /// <summary>Один замер выбранным путём; результат рисуется по мере прихода этапов.</summary>
    private async Task<SiteResult?> MeasureAsync(Uri uri, bool tunnel, CancellationToken ct)
    {
        if (tunnel && !_tunnelUp)
        {
            ShowSiteError("Туннель не запущен: запустите движки на «Главной».");
            return null;
        }

        var proxy = tunnel ? EngineKeys.Loopback(SingBoxOptions.DefaultHealthPort, EngineKeys.Current()) : null;

        // Progress создан в потоке окна и показывает этапы там же.
        var progress = new Progress<SiteProgress>(p =>
        {
            if (_site is null)
                return;

            SiteStatus.Text = p.Status;
            ShowSite(p.Partial, final: false);
        });

        // Сеть — вне потока окна.
        return await Task.Run(() => SiteSpeed.RunAsync(uri, proxy, progress, ct), ct);
    }

    private void FinishSite(SiteResult r)
    {
        ShowSite(r, final: true);

        if (r.Fail != SiteFail.None)
        {
            ShowSiteError(r.Fail switch
            {
                SiteFail.Dns => "Не удалось найти адрес. Проверьте написание",
                SiteFail.Tcp => "Сайт не принимает соединения",
                SiteFail.Tls => "Не удалось установить защищённое соединение",
                SiteFail.Timeout => "Сайт не ответил за 8 секунд",
                _ => "Сайт не отдал страницу",
            });

            SiteGo.Content = "Повторить";
            SiteAgain.Visibility = Visibility.Collapsed;
            SiteCopy.Visibility = Visibility.Collapsed;
            return;
        }

        SiteAgain.Visibility = SiteCopy.Visibility = Visibility.Visible;

        // Что медленнее всего — одной строкой: за ней и идут.
        var stages = new (string Name, double? Ms)[] { ("DNS", r.DnsMs), ("TCP", r.TcpMs), ("TLS", r.TlsMs), ("ответ сервера", r.TtfbMs) }
            .Where(s => s.Ms is not null).OrderByDescending(s => s.Ms).ToList();

        _siteText = (stages.Count > 0 ? $"Медленнее всего {stages[0].Name}: {stages[0].Ms:0} мс. " : string.Empty)
            + (r.DownMbps is { } d ? $"Скачивание {Mbps(d)}" : "Страница слишком мала для замера скорости");

        SiteSummary.Text = _siteText;
    }

    private async Task CompareAsync(Uri uri, CancellationToken ct)
    {
        SiteStatus.Text = "Измеряю напрямую…";
        var direct = await MeasureAsync(uri, false, ct);

        if (direct is null)
            return;

        ShowSite(direct, final: true);

        SiteStatus.Text = "Измеряю через туннель…";
        SiteResult? tunnel = _tunnelUp ? await MeasureAsync(uri, true, ct) : null;

        CompareCard.Visibility = Visibility.Visible;
        CmpDirect.Text = CompareText(direct, tunnel);
        CmpTunnel.Text = tunnel is null ? "Туннель выключен. Запустите движки на «Главной»." : CompareText(tunnel, direct);

        string key = "Text";
        string verdict;

        bool directOk = direct.Fail == SiteFail.None, tunnelOk = tunnel is { Fail: SiteFail.None };

        if (tunnel is null)
        {
            verdict = "Сравнение недоступно: туннель выключен";
            key = "Muted";
        }
        else if (directOk && tunnelOk)
        {
            double a = direct.TotalMs ?? 0, b = tunnel!.TotalMs ?? 0;
            double share = Math.Max(a, b) > 0 ? Math.Abs(a - b) / Math.Max(a, b) : 0;

            (verdict, key) = share < 0.10 ? ("Разница незаметна", "Text")
                : b < a ? ($"Через туннель быстрее на {share * 100:0}%", "Success")
                : ($"Напрямую быстрее на {share * 100:0}%", "Success");
        }
        else if (!directOk && tunnelOk)
        {
            (verdict, key) = ("Напрямую сайт не открывается, через туннель работает", "Warn");
        }
        else if (directOk)
        {
            (verdict, key) = ("Через туннель сайт не открывается", "Warn");
        }
        else
        {
            (verdict, key) = ("Не открывается ни одним путём", "Danger");
        }

        CmpVerdict.Text = verdict;
        CmpVerdict.SetResourceReference(TextBlock.ForegroundProperty, key);

        // Тот самый случай: прямо обрывается на соединении, туннель проходит.
        SiteBlock.Visibility = !directOk && tunnelOk && direct.Fail is SiteFail.Tcp or SiteFail.Tls or SiteFail.Timeout
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Основной результат — прямой путь; он же копируется.
        FinishSite(direct);

        if (direct.Fail != SiteFail.None)
            SiteResultGrid.Visibility = Visibility.Visible;
    }

    /// <summary>Строки одного пути в сравнении; «▲» — значение лучше, чем у другого пути.</summary>
    private static string CompareText(SiteResult mine, SiteResult? other)
    {
        if (mine.Fail != SiteFail.None)
            return mine.Fail switch
            {
                SiteFail.Dns => "Адрес не найден",
                SiteFail.Tcp => "Не принимает соединения",
                SiteFail.Tls => "Защищённое соединение не установилось",
                SiteFail.Timeout => "Не ответил за 8 секунд",
                _ => "Страница не отдалась",
            };

        bool Better(double? a, double? b, bool lowerIsBetter = true) =>
            a is { } x && other is { Fail: SiteFail.None } && b is { } y && (lowerIsBetter ? x < y : x > y);

        string Line(string label, string? value, bool better) => $"{label}: {value ?? "—"}{(better ? " ▲ лучше" : string.Empty)}";

        return string.Join("\n",
            Line("Ответ (TTFB)", mine.TtfbMs is { } t ? $"{t:0} мс" : null, Better(mine.TtfbMs, other?.TtfbMs)),
            Line("Загрузка", mine.DownMbps is { } d ? Mbps(d) : "—", Better(mine.DownMbps, other?.DownMbps, lowerIsBetter: false)),
            Line("Всего", mine.TotalMs is { } total ? $"{total:0} мс" : null, Better(mine.TotalMs, other?.TotalMs)));
    }

    private static string Mbps(double v) => v.ToString(v >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " Мбит/с";

    /// <summary>Маршрут Zephyron для имени — теми же правилами, что <c>zy where</c> (RouteWhy).</summary>
    /// <returns>Ведёт ли маршрут имя в туннель.</returns>
    private async Task<bool> ShowRouteAsync(string host)
    {
        var (chip, why) = await Task.Run(() =>
        {
            try
            {
                var settings = AppSettings.Load(AppSettings.DefaultPath);
                var mode = RouteWhy.ModeOf(host, settings);
                var effective = settings.Engines.Effective(mode, settings.NeedsProxy);
                var lines = RouteWhy.Explain(host, settings);

                return (effective switch
                {
                    RoutingMode.Proxy => "Через туннель",
                    RoutingMode.Desync => "Десинк",
                    _ => "Напрямую",
                }, lines.Count > 0 ? Regex.Replace(lines[0], @"\s+", " ").Trim() : string.Empty);
            }
            catch (Exception)
            {
                // Правила не прочитались — маршрут не называем, а не выдумываем.
                return ("—", string.Empty);
            }
        });

        if (!IsLoaded)
            return chip == "Через туннель";

        RouteVal.Text = chip;
        RouteWhyText.Text = why;
        RouteChip.SetResourceReference(Border.BackgroundProperty, chip switch { "Десинк" => "AccentSoft", _ => "Wash" });
        RouteChip.SetResourceReference(Border.BorderBrushProperty, chip == "Через туннель" ? "Accent2" : "Border");
        return chip == "Через туннель";
    }

    /// <summary>Этапы на экране. Пока идёт замер, ещё не пришедшее — «…»; по итогу — «—».</summary>
    private void ShowSite(SiteResult r, bool final)
    {
        string Wait(double? v, string format) => v is { } x ? string.Format(CultureInfo.InvariantCulture, format, x) : final ? "—" : "…";

        // Через туннель три первых этапа по ту сторону — их нет.
        string Inside(double? v) => r.Tunnel && v is null ? "—" : Wait(v, "{0:0} мс");

        DnsVal.Text = Inside(r.DnsMs);
        TcpVal.Text = Inside(r.TcpMs);
        TlsVal.Text = Inside(r.TlsMs);
        TtfbVal.Text = Wait(r.TtfbMs, "{0:0} мс");
        DownVal.Text = r.DownMbps is { } d ? Mbps(d) : final ? "—" : "…";

        Colour(DnsVal, r.DnsMs, 200);
        Colour(TcpVal, r.TcpMs, 150);
        Colour(TlsVal, r.TlsMs, 200);
        Colour(TtfbVal, r.TtfbMs, 600);

        TileDns.ToolTip = r.Tunnel ? "Внутри туннеля имя разрешается на его стороне — не измеряется" : "Время разрешения имени";
        TileDown.ToolTip = final && r.DownMbps is null && r.Bytes > 0
            ? $"Страница слишком мала для замера скорости ({r.Bytes / 1024} КБ)"
            : "Скорость скачивания страницы";

        AutomationSet(TileDns, "DNS", DnsVal);
        AutomationSet(TileTcp, "TCP", TcpVal);
        AutomationSet(TileTls, "TLS", TlsVal);
        AutomationSet(TileTtfb, "Ответ", TtfbVal);
        AutomationSet(TileDown, "Загрузка", DownVal);

        // Полоса: ширина сегмента — время этапа; нет этапа — нет сегмента.
        SiteBar.ColumnDefinitions.Clear();
        SiteBar.Children.Clear();

        var segments = new (double? Ms, string Brush)[]
        {
            (r.DnsMs, "Accent2"), (r.TcpMs, "Accent"), (r.TlsMs, "Success"), (r.TtfbMs, "Warn"),
            (r.DownMbps is not null ? r.Bytes * 8 / (r.DownMbps * 1000) : null, "Muted"),
        };

        foreach (var (ms, brush) in segments.Where(s => s.Ms is > 0))
        {
            SiteBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ms!.Value, GridUnitType.Star) });

            var part = new Border();
            part.SetResourceReference(Border.BackgroundProperty, brush);
            Grid.SetColumn(part, SiteBar.ColumnDefinitions.Count - 1);
            SiteBar.Children.Add(part);
        }

        _siteIp = r.Ip;
        IpVal.Text = r.Ip ?? "—";
        IpCopy.Visibility = r.Ip is null ? Visibility.Collapsed : Visibility.Visible;

        HttpVal.Text = r.Http is null ? "—" : r.H3Hint && r.Http != "HTTP/3" ? r.Http + " (объявлен HTTP/3)" : r.Http;
        ServerVal.Text = final ? r.Server ?? "не сообщает" : r.Server ?? "—";
        ServerVal.ToolTip = r.Server;

        JitterVal.Text = r.JitterMs is { } j ? $"{j:0.0} мс" : "—";

        if (r.Grade is { } grade)
        {
            GradeBox.Visibility = Visibility.Visible;
            GradeLetter.Text = grade.ToString();
            GradeLetter.SetResourceReference(TextBlock.ForegroundProperty, grade is 'A' or 'B' ? "Success" : grade == 'C' ? "Warn" : "Danger");
            LoadedVal.Text = $"+{r.LoadedMs:0} мс при загрузке";
            LoadedVal.ToolTip = "Рост времени соединения, пока качается страница: чем меньше, тем лучше держит нагрузку";
        }
        else
        {
            GradeBox.Visibility = Visibility.Collapsed;
            LoadedVal.Text = final && !r.Tunnel ? "страница слишком короткая" : "—";
            LoadedVal.ToolTip = r.Tunnel ? "Через туннель не измеряется: соединение до сайта по ту сторону" : null;
        }
    }

    private static void AutomationSet(Border tile, string name, TextBlock value) =>
        System.Windows.Automation.AutomationProperties.SetName(tile, $"{name} {value.Text}");

    /// <summary>Значение жёлтым после порога и красным после удвоенного; нет значения — обычным.</summary>
    private static void Colour(TextBlock block, double? ms, double limit) =>
        block.SetResourceReference(TextBlock.ForegroundProperty, ms is not { } v ? "Text" : v > limit * 2 ? "Danger" : v > limit ? "Warn" : "Text");

    private void ShowSiteError(string text)
    {
        SiteErrorText.Text = text;
        SiteError.Visibility = Visibility.Visible;
        SiteGo.Content = "Повторить";
    }

    private void OnIpCopy(object sender, RoutedEventArgs e) => Copy(_siteIp, IpCopy);

    private void OnSiteCopy(object sender, RoutedEventArgs e) => Copy(_siteText, SiteCopy);

    private async void Copy(string? text, Button source)
    {
        if (string.IsNullOrEmpty(text))
            return;

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception)
        {
            // Буфер занят другой программой — не критично, нажмут ещё раз.
            return;
        }

        var tip = source.ToolTip;
        source.ToolTip = "Скопировано";
        await Task.Delay(1500);
        source.ToolTip = tip;
    }

    private void OnSiteRoutes(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as MainWindow)?.Open("routes");

    /// <summary>Две колонки результата от 700 точек, ниже — одна; плитки по пять, на узкой по три.</summary>
    private void SiteLayout(double width)
    {
        bool wide = width >= 700;

        SiteGap.Width = new GridLength(wide ? 24 : 0);
        SiteSide.Width = new GridLength(wide ? 300 : 0);
        Grid.SetRow(SiteFacts, wide ? 0 : 1);
        Grid.SetColumn(SiteFacts, wide ? 2 : 0);
        Grid.SetColumnSpan(SiteFacts, wide ? 1 : 3);
        SiteFacts.Margin = new Thickness(0, wide ? 0 : 12, 0, 0);
        SiteTiles.Columns = width >= 520 ? 5 : 3;
    }
}
