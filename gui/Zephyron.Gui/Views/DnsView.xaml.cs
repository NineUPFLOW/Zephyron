using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Zephyron.Core;
using Zephyron.Proxy;
using Zephyron.Supervisor;

namespace Zephyron.Gui.Views;

/// <summary>Резолвер в списке выбора.</summary>
public sealed class ResolverRow
{
    public required string Name { get; init; }
    public required string Address { get; init; }
    public required string Note { get; init; }

    /// <summary>Отклик либо причина молчания; до замера — прочерк.</summary>
    public string Latency { get; set; } = "—";

    public string Key { get; set; } = "Faint";

    public bool Chosen { get; set; }

    /// <summary>Свой резолвер (<see cref="CustomDns"/>) — его можно убрать.</summary>
    public bool Own { get; init; }

    /// <summary>Годится ли в апстрим туннеля: у своего без DoH — нет.</summary>
    public bool Choosable { get; init; } = true;

    /// <summary>Адрес обычного DNS — по нему свой резолвер и убирается.</summary>
    public string Udp { get; init; } = string.Empty;

    /// <summary>Раздел списка; заголовок показывает первая строка раздела.</summary>
    public DnsGroup Group { get; init; } = DnsGroup.Popular;

    public bool FirstInGroup { get; init; }

    public string HeadTitle => DnsMarks.Title(Group);

    public string HeadHint => DnsMarks.Hint(Group);

    public Visibility HeadShown => FirstInGroup ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Пометки по обзору: «блокируется», «UDP подменяют».</summary>
    public IReadOnlyList<string> Marks { get; set; } = [];

    public Visibility RemoveShown => Own ? Visibility.Visible : Visibility.Collapsed;

    public Brush Color => (Brush)Application.Current.FindResource(Key);

    public Brush Edge => (Brush)Application.Current.FindResource(Chosen ? "AccentLine" : "Border");

    public Visibility MarkShown => Chosen ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Ярлыки из пояснения провайдера: «надёжный, без фильтрации» — два ярлыка.
    /// </summary>
    public IReadOnlyList<string> Tags => Note
        .Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(tag => char.ToUpper(tag[0]) + tag[1..])
        .Concat(Marks)
        .ToList();

    /// <summary>Логотип с сайта резолвера; <c>null</c> — вместо него буква.</summary>
    public System.Windows.Media.Imaging.BitmapImage? Icon { get; set; }

    /// <summary>Сайт, с которого берётся логотип; <c>null</c> — брать неоткуда.</summary>
    public string? Site { get; init; }

    public string Letter => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "·";

    public Visibility IconShown => Icon is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility LetterShown => Icon is null ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>Режим шифрованного DNS в карточке DNSCrypt.</summary>
public sealed class CryptRow
{
    public required DnsCryptMode Mode { get; init; }
    public string Title => DnsCrypt.Describe(Mode).Title;
    public string Note => DnsCrypt.Describe(Mode).Note;
    public string Latency { get; set; } = "—";
    public bool Chosen { get; set; }
    public bool Available { get; init; }
    public Brush Edge => (Brush)Application.Current.FindResource(Chosen ? "AccentLine" : "Border");
}

/// <summary>Строка обзора резолверов.</summary>
/// <remarks>
/// Цвета — по смыслу, как на снимке владельца: зелёное прошло, красное
/// перехвачено или подменено, серое не мерилось.
/// </remarks>
public sealed class SurveyRow
{
    private SurveyRow(DnsSurveyRow row) => Source = row;

    public DnsSurveyRow Source { get; }

    public string Name => Source.Provider.Name;

    public string Doh => Ms(Source.DohMs, Source.DohFailure);

    public string Dot => Ms(Source.DotMs, Source.DotFailure);

    public string Udp
    {
        get
        {
            var text = Ms(Source.UdpMs, Source.UdpFailure);

            return Source.UdpMs is not null && Source.UdpAnswered < Source.Provider.Udp.Count
                ? $"{text} {Source.UdpAnswered}/{Source.Provider.Udp.Count}"
                : text;
        }
    }

    public string Real => Source.RealResolver is null
        ? "—"
        : $"{Source.RealResolver} → {Source.RealNetwork ?? "?"}";

    public string Spoof => Source.SpoofChecked == 0 ? "—" : $"{Source.Spoofed}/{Source.SpoofChecked}";

    public Brush DohColor => Paint(Source.DohMs, Source.DohFailure);

    public Brush DotColor => Paint(Source.DotMs, Source.DotFailure);

    public Brush UdpColor => Paint(Source.UdpMs, Source.UdpFailure);

    public Brush RealColor => Brush(Source.RealResolver is null ? "Faint" : Source.Intercepted ? "Danger" : "Success");

    public Brush SpoofColor => Brush(Source.SpoofChecked == 0 ? "Faint" : Source.Spoofed > 0 ? "Danger" : "Success");

    public static SurveyRow From(DnsSurveyRow row) => new(row);

    private static string Ms(double? ms, string failure) =>
        ms is { } value ? $"{value:0.0} мс" : failure.Length > 0 ? failure : "—";

    private static Brush Paint(double? ms, string failure) =>
        Brush(ms is not null ? "Success" : failure.Length > 0 ? "Danger" : "Faint");

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
}

/// <summary>
/// Апстрим DNS туннеля.
/// </summary>
/// <remarks>
/// <para>
/// Речь только о том резолвере, которого спрашивает sing-box. Настройки DNS
/// самой Windows не трогаются ни здесь, ни где-либо ещё: подмена системного
/// резолвера ломает то, что от него зависит — корпоративные имена, принтеры,
/// сетевые диски, — и переживает удаление программы.
/// </para>
/// <para>
/// Замер — один, обзор резолверов (<see cref="DnsSurvey"/>): настоящими
/// запросами DoH, DoT и UDP через адаптер, мимо туннеля. Прежде рядом
/// стояла вторая кнопка «Проверить» с замером одного DoH по шести
/// резолверам — обзор меряет то же и больше, и две кнопки только путали.
/// </para>
/// </remarks>
public partial class DnsView : UserControl
{
    private CancellationTokenSource? _work;

    /// <summary>Последний обзор за сеанс: строки, итог словами и время.</summary>
    private sealed record LastSurvey(IReadOnlyList<DnsSurveyRow> Rows, string Status, DateTime At);

    /// <summary>
    /// Последний обзор — общий для всех заходов на вкладку.
    /// </summary>
    /// <remarks>
    /// Вкладка пересоздаётся при каждом заходе, и задержки, намеренные
    /// обзором, пропадали, стоило уйти на другую вкладку и вернуться
    /// (владелец, 04.10). Только на время работы окна: задержка с прошлого
    /// запуска уже ни о чём не говорит.
    /// </remarks>
    private static LastSurvey? _last;

    /// <summary>Показывает последний обзор: таблицу и задержки в списке.</summary>
    private void ShowLast()
    {
        if (_last is not { } last)
            return;

        var choices = Resolvers.ItemsSource as IReadOnlyList<ResolverRow> ?? [];

        foreach (var row in last.Rows)
            Fill(choices, row);

        Survey.ItemsSource = last.Rows.Select(SurveyRow.From).ToList();
        SurveyHead.Visibility = Visibility.Visible;
        SurveyStatus.Text = $"{last.Status} Проверено в {last.At:HH:mm}.";
        Status.Text = $"Отклик взят из сводки в {last.At:HH:mm}.";

        Redraw();
    }

    private CancellationTokenSource? _logos;

    /// <summary>
    /// Сайт, чей значок — логотип резолвера.
    /// </summary>
    /// <remarks>
    /// Значок берётся у самого сайта (SiteIcons), как у сервисов в «Маршрутах».
    /// Имя DoH для этого годится не всегда: у dns.quad9.net и
    /// common.dot.dns.yandex.net значка нет, он на главном сайте. Неизвестным
    /// и своим — по имени DoH без первой части; нет и его — буква.
    /// Буква и у XBOX DNS (значок только SVG, окно его не рисует) — замер 05.10.
    /// </remarks>
    private static string? LogoSite(DnsProvider provider)
    {
        var name = provider.Name;

        if (name.StartsWith("Google", StringComparison.OrdinalIgnoreCase)) return "dns.google";
        if (name.StartsWith("Cloudflare", StringComparison.OrdinalIgnoreCase)) return "one.one.one.one";
        if (name.StartsWith("Quad9", StringComparison.OrdinalIgnoreCase)) return "quad9.net";
        if (name.StartsWith("AdGuard", StringComparison.OrdinalIgnoreCase)) return "adguard-dns.io";
        if (name.StartsWith("Yandex", StringComparison.OrdinalIgnoreCase)) return "dns.yandex.ru";
        // С www: голый opendns.com переадресует на http://www… — с https на http
        // загрузчик не идёт, и логотипа не было (05.10).
        if (name.StartsWith("OpenDNS", StringComparison.OrdinalIgnoreCase)) return "www.opendns.com";
        if (name.StartsWith("CleanBrowsing", StringComparison.OrdinalIgnoreCase)) return "cleanbrowsing.org";
        if (name.StartsWith("NextDNS", StringComparison.OrdinalIgnoreCase)) return "nextdns.io";
        if (name.StartsWith("ControlD", StringComparison.OrdinalIgnoreCase)) return "controld.com";
        if (name.StartsWith("Alibaba", StringComparison.OrdinalIgnoreCase)) return "alidns.com";

        if (provider.TlsName is not { } tls)
            return null;

        var labels = tls.Split('.');

        return labels.Length > 2 ? string.Join('.', labels[^2..]) : tls;
    }

    /// <summary>Логотипы: из кэша сразу, остальные — в фоне, по одному.</summary>
    private void StartLogos()
    {
        _logos?.Cancel();
        _logos = new CancellationTokenSource();

        var token = _logos.Token;
        var rows = Resolvers.ItemsSource as IReadOnlyList<ResolverRow> ?? [];

        foreach (var row in rows)
            row.Icon = row.Site is { } site ? SiteIcons.Cached(site) : null;

        Redraw();

        _ = Task.Run(async () =>
        {
            foreach (var row in rows)
            {
                if (token.IsCancellationRequested)
                    return;

                if (row.Site is not { } site || SiteIcons.Known(site))
                    continue;

                var icon = await SiteIcons.ForAsync(site, token);

                if (icon is null || token.IsCancellationRequested)
                    continue;

                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (token.IsCancellationRequested)
                        return;

                    row.Icon = icon;
                    Redraw();
                });
            }
        }, token);
    }

    public DnsView()
    {
        InitializeComponent();
        StatusLook.Attach(StatusBox, Status, StatusMark);
        StatusLook.Attach(OwnStatusBox, OwnStatus, OwnStatusMark);

        Loaded += (_, _) => Reload();
        Unloaded += (_, _) =>
        {
            _work?.Cancel();
            _cryptWork?.Cancel();
            _logos?.Cancel();
        };
    }

    private void Reload()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        ShowChosen(settings);
        ShowSystem();
        ShowPinWatch(settings);

        var verdicts = ResolverVerdicts.Load();

        // Те же провайдеры, что в обзоре, — все, кого sing-box может
        // спрашивать по DoH. Прежде список был своим, из шести, и мерился
        // своей кнопкой: две «Проверить» на одной вкладке, и чем они
        // отличаются, было не понять.
        //
        // Свои — и без DoH тоже: выбрать такой туннелю нельзя, но убрать
        // надо где-то уметь, а спрятанный он остался бы в файле навсегда.
        Resolvers.ItemsSource = DnsMarks.Sections(DnsSurvey.All.Where(p => p.Choosable || p.Own))
            .SelectMany(section => section.Items.Select((p, n) => new ResolverRow
            {
                Group = section.Group,
                FirstInGroup = n == 0,
                Name = p.Name,
                Address = p.TlsAddress ?? p.Udp[0],
                Note = p.Note ?? string.Empty,
                Chosen = !settings.UseRouterDns && settings.DnsCryptActive is null && p.Choosable && string.Equals(p.TlsAddress, settings.DnsServer, StringComparison.Ordinal),
                Own = p.Own,
                Choosable = p.Choosable,
                Udp = p.Udp.Count > 0 ? p.Udp[0] : string.Empty,
                Site = LogoSite(p),
                Marks = VerdictMarks(verdicts, p),
            }))
            .ToList();

        ShowNow(settings);
        ShowCrypt(settings);

        Status.Text = "Отклик покажет сводка выше.";

        ShowLast();

        StartLogos();
    }

    /// <summary>Пометка по запомненному вердикту обзора — пока свежий замер её не заменил.</summary>
    private static IReadOnlyList<string> VerdictMarks(IReadOnlyDictionary<string, ResolverVerdict> verdicts, DnsProvider p) =>
        verdicts.TryGetValue(ResolverVerdicts.Key(p), out var v)
            ? [v.Kind switch
            {
                ResolverVerdictKind.Blocked => DnsMarks.Blocked,
                ResolverVerdictKind.UdpSpoofed => DnsMarks.UdpSpoofed,
                _ => "не тронут",
            }]
            : [];

    /// <summary>Строка «куда сейчас идут имена».</summary>
    private void ShowNow(AppSettings settings)
    {
        try
        {
            NowLine.Text = DnsNow.Describe(settings, SupervisorState.Load(SupervisorState.DefaultPath), null);
        }
        catch (Exception ex)
        {
            NowLine.Text = "Куда идут имена, прочитать не удалось: " + ex.GetBaseException().Message;
        }
    }

    private CancellationTokenSource? _cryptWork;

    private void ShowCrypt(AppSettings settings, IReadOnlyList<DnsCryptLatency>? latency = null)
    {
        bool bundled = File.Exists(DnsCrypt.Executable());

        CryptModes.ItemsSource = DnsCrypt.Modes.Select(mode => new CryptRow
        {
            Mode = mode,
            Available = bundled,
            Chosen = settings.DnsCryptActive == mode,
            Latency = latency?.FirstOrDefault(l => l.Mode == mode) is { } l
                ? l.Ms is { } ms ? $"{ms:0} мс" : l.Failure
                : "—",
        }).ToList();

        CryptStatus.Text = !bundled
            ? "dnscrypt-proxy не входит в эту сборку (нет папки engines\\dnscrypt), режимы недоступны."
            : settings.UseRouterDns && settings.DnsCrypt is { Length: > 0 }
                ? "Включён DNS роутера: выбор режима сохранится, но подействует после его выключения."
                : "Нажмите на режим, чтобы включить; повторное нажатие на выбранный выключает. Туннель спрашивает dnscrypt-proxy на этом компьютере.";
        CryptMeasure.IsEnabled = bundled;
    }

    private void OnCryptChoose(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: CryptRow row })
            return;

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            bool off = row.Chosen;
            var next = settings with { DnsCrypt = off ? null : DnsCrypt.Key(row.Mode) };
            next.Save(AppSettings.DefaultPath);

            ShowCrypt(next);
            ShowChosen(next);
            ShowNow(next);

            if (Resolvers.ItemsSource is IEnumerable<ResolverRow> rows)
            {
                foreach (var r in rows)
                    r.Chosen = off && !next.UseRouterDns && r.Address == next.DnsServer;

                Redraw();
            }

            Status.Text = off ? "DNSCrypt выключен. Вступит в силу после следующего запуска движков."
                : $"{row.Title}: вступит в силу после следующего запуска движков.";
            this.Offer(off ? "DNSCrypt выключен" : $"DNS: {row.Title}");
        }
        catch (Exception ex)
        {
            CryptStatus.Text = "Выбор не записался: " + ex.GetBaseException().Message;
        }
    }

    private async void OnCryptMeasure(object sender, RoutedEventArgs e)
    {
        _cryptWork?.Cancel();
        _cryptWork = new CancellationTokenSource();
        var token = _cryptWork.Token;

        CryptMeasure.IsEnabled = false;
        CryptStatus.Text = "Меряю три режима: временный dnscrypt-proxy поднимается до 15 секунд на режим…";

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            var result = await DnsCryptProbe.MeasureAllAsync(settings.DnsCryptActive, token);

            if (token.IsCancellationRequested)
                return;

            ShowCrypt(settings, result);
            CryptStatus.Text = "Задержка — середина трёх ответов на разные имена.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            CryptStatus.Text = "Замер не удался: " + ex.GetBaseException().Message;
            CryptMeasure.IsEnabled = true;
        }
    }

    private void ShowChosen(AppSettings settings)
    {
        var known = DnsSurvey.ByAddress(settings.DnsServer);

        ChosenName.Text = known is null
            ? settings.DnsServer
            : $"{known.Name} · {known.TlsAddress}";

        if (settings.UseRouterDns)
            ChosenName.Text = $"Роутер · {settings.RouterDnsAddress}:53 · UDP/LAN";
        else if (settings.DnsAuto)
            ChosenName.Text += " · автовыбор";
        var fallback = RouterDnsConnection.ConfigWarning(settings);
        if (fallback is not null)
        {
            ChosenName.Text = "Запасной DoH · DNS роутера в текущем конфиге не используется";
            ShowRouterStatus(fallback, RouterDnsFit.Unknown);
        }
        else if (!settings.UseRouterDns)
        {
            ShowRouterStatus("Перед включением программа проверит роутер: отвечает ли он и не подменяет ли заблокированные имена.", null);
        }
        else if (_lastCheck is { } last && last.Address == settings.RouterDnsAddress)
        {
            ShowRouterStatus(Enabled(last.Check), last.Check.Fit);
        }
        else
        {
            // Включён до этого запуска окна: проверить заново, чтобы предупреждение
            // о подмене не пропадало с перезапуском. Раз на адрес за запуск.
            ShowRouterStatus("Включён. Заработает после перезапуска движков.", null);
            _lastCheck = null;
            if (_recheckedFor != settings.RouterDnsAddress)
            {
                _recheckedFor = settings.RouterDnsAddress;
                _ = CheckRouterAsync(settings.RouterDnsAddress, enabled: true);
            }
        }
        RouterDnsSwitch.IsChecked = settings.UseRouterDns;
        RouterDnsIp.Text = settings.RouterDnsAddress;
        TunnelMode.IsEnabled = !settings.UseRouterDns;

        // Автовыбор при DNS роутера хранится, но не действует (RouterDnsSettings.Select).
        AutoDnsSwitch.IsChecked = settings.DnsAuto && !settings.UseRouterDns;

        // Заполняем, не поднимая события выбора: иначе показ состояния
        // тут же записал бы его обратно в настройки и позвал уведомление
        // о перезапуске — при каждом заходе на вкладку.
        _filling = true;
        TunnelMode.SelectedIndex = settings.DnsVia switch
        {
            DnsRoute.Direct => 1,
            DnsRoute.Tunnel => 2,
            _ => 0,
        };
        _filling = false;

        // Цена пути — те же слова, что в мастере первого запуска.
        TunnelHint.Text = DnsRoutes.Explain(settings);
    }

    /// <summary>
    /// Последняя проверка роутера за этот запуск окна — чтобы предупреждение
    /// стояло и при новом заходе на вкладку (вкладка создаётся заново).
    /// </summary>
    private static (string Address, RouterDnsCheck Check)? _lastCheck;

    /// <summary>Адрес, который уже перепроверялся при показе вкладки.</summary>
    private static string? _recheckedFor;

    /// <summary>Подпись под карточкой: жёлтая — предупреждение, красная — отказ.</summary>
    private void ShowRouterStatus(string text, RouterDnsFit? fit)
    {
        RouterDnsStatus.Text = text;

        if (fit is RouterDnsFit.Spoofs or RouterDnsFit.Unknown)
            RouterDnsStatus.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
        else if (fit is RouterDnsFit.Silent)
            RouterDnsStatus.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        else
            RouterDnsStatus.ClearValue(TextBlock.ForegroundProperty);
    }

    /// <summary>Итог проверки для включённого DNS роутера.</summary>
    private static string Enabled(RouterDnsCheck check) => check.Fit switch
    {
        RouterDnsFit.Spoofs => check.Text + " DNS роутера всё равно включён: если сайты под десинком или напрямую перестанут открываться, выключите его.",
        RouterDnsFit.Silent => check.Text + " DNS роутера включён, но сейчас не отвечает.",
        _ => check.Text + " DNS роутера включён — действует после перезапуска движков.",
    };

    /// <summary>Проверить роутер и включить его DNS — с предупреждением, если он подменяет.</summary>
    /// <remarks>
    /// Включение идёт через проверку: роутер, подменяющий закрытые имена,
    /// молча закрыл бы сайты под десинком (замер 15.10 у владельца,
    /// <see cref="RouterDnsConnection.CheckAsync"/>). Не запрещает — только
    /// молчащий роутер (<see cref="RouterDnsCheck.Allows"/>).
    /// </remarks>
    private async Task<bool> SaveRouterDnsAsync()
    {
        if (!RouterDnsSettings.TryNormalize(RouterDnsIp.Text, out var address))
        {
            ShowRouterStatus("Введите адрес роутера в домашней сети, например 192.168.1.1. IPv6, имена и публичные DNS здесь не подходят.", RouterDnsFit.Silent);
            return false;
        }

        var check = await CheckRouterAsync(address, enabled: false);
        if (check is null || !check.Allows)
            return false;

        try
        {
            var settings = RouterDnsSettings.Select(AppSettings.Load(), address);
            settings.Save(AppSettings.DefaultPath);
            ShowChosen(settings);
            Status.Text = check.Warns
                ? "DNS роутера включён с предупреждением, см. карточку. Перезапустите движки."
                : "DNS роутера включён. Перезапустите движки.";
            this.Offer("DNS роутера включён");
            return true;
        }
        catch (Exception ex)
        {
            ShowRouterStatus("Сохранить не удалось: " + ex.GetBaseException().Message, RouterDnsFit.Silent);
            return false;
        }
    }

    /// <summary>Проверка роутера с запертыми на её время кнопками; <c>null</c> — сорвалась.</summary>
    /// <param name="enabled">DNS роутера уже включён — подпись говорит о включённом.</param>
    private async Task<RouterDnsCheck?> CheckRouterAsync(string address, bool enabled)
    {
        RouterDnsSwitch.IsEnabled = RouterDnsUse.IsEnabled = RouterDnsTest.IsEnabled = false;
        ShowRouterStatus("Проверяю роутер: отвечает ли он и не подменяет ли заблокированные имена…", null);
        try
        {
            var check = await RouterDnsConnection.CheckAsync(address, CancellationToken.None);
            _lastCheck = (address, check);
            ShowRouterStatus(enabled ? Enabled(check) : check.Allows ? check.Text : check.Text + " DNS роутера не включён.", check.Fit);
            return check;
        }
        catch (Exception ex)
        {
            ShowRouterStatus("Проверку выполнить не удалось: " + ex.GetBaseException().Message, RouterDnsFit.Silent);
            return null;
        }
        finally
        {
            RouterDnsSwitch.IsEnabled = RouterDnsUse.IsEnabled = RouterDnsTest.IsEnabled = true;
        }
    }

    private async void OnRouterDnsUse(object sender, RoutedEventArgs e)
    {
        if (await SaveRouterDnsAsync())
            RouterDnsSwitch.IsChecked = true;
    }

    private async void OnRouterDnsToggle(object sender, RoutedEventArgs e)
    {
        if (RouterDnsSwitch.IsChecked == true)
        {
            // Выключатель встаёт сам, только когда проверка пропустила.
            RouterDnsSwitch.IsChecked = false;
            RouterDnsSwitch.IsChecked = await SaveRouterDnsAsync();
            return;
        }
        try
        {
            var settings = AppSettings.Load() with { UseRouterDns = false };
            settings.Save(AppSettings.DefaultPath);
            ShowChosen(settings);
            ShowRouterStatus($"DNS роутера выключен. Вернулись прежние: {ChosenName.Text}, путь — {DnsRoutes.Word(settings.DnsVia)}. Перезапустите движки.", null);
            this.Offer("DNS роутера выключен");
        }
        catch (Exception ex)
        { RouterDnsSwitch.IsChecked = true; ShowRouterStatus("Сохранить не удалось: " + ex.GetBaseException().Message, RouterDnsFit.Silent); }
    }

    private async void OnRouterDnsTest(object sender, RoutedEventArgs e)
    {
        if (!RouterDnsSettings.TryNormalize(RouterDnsIp.Text, out var address))
        { ShowRouterStatus("Введите адрес роутера в домашней сети, например 192.168.1.1.", RouterDnsFit.Silent); return; }

        var enabled = AppSettings.Load() is { UseRouterDns: true } now && now.RouterDnsAddress == address;
        await CheckRouterAsync(address, enabled);
    }

    /// <summary>Показывает, кого спрашивает сама Windows.</summary>
    private void ShowSystem()
    {
        try
        {
            var found = SystemResolvers.Discover();

            SystemValue.Text = found.Count == 0
                ? "не найдены"
                : string.Join(", ", found);
        }
        catch (Exception ex)
        {
            SystemValue.Text = "не удалось прочитать: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>Задержка DoH из обзора — в строку списка выбора.</summary>
    private static void Fill(IReadOnlyList<ResolverRow> rows, DnsSurveyRow result)
    {
        var row = rows.FirstOrDefault(r => r.Address == result.Provider.TlsAddress);

        if (row is null)
            return;

        row.Marks = DnsMarks.Of(result);

        if (result.DohMs is { } ms)
        {
            row.Latency = $"{ms:0} мс";

            // Порог не про качество связи, а про ощущение: до полусекунды
            // задержка резолвера теряется в открытии страницы, дальше уже
            // заметна на каждом новом имени.
            row.Key = ms < 500 ? "Success" : "Warn";

            return;
        }

        row.Latency = result.DohFailure.Length > 0 ? result.DohFailure : "не откликается";
        row.Key = "Danger";
    }
    private void Redraw()
    {
        var shown = Resolvers.ItemsSource;

        Resolvers.ItemsSource = null;
        Resolvers.ItemsSource = shown;
    }

    private void OnChoose(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string address })
            return;

        if (sender is FrameworkElement { DataContext: ResolverRow { Choosable: false } refused })
        {
            Status.Text = $"{refused.Name} без DoH, а туннель спрашивает только по DoH — выбрать его нельзя. "
                + "Уберите и добавьте заново с именем DoH, если резолвер его умеет.";
            return;
        }

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath) with { DnsServer = address, DnsAuto = false, UseRouterDns = false, DnsCrypt = null };
            settings.Save(AppSettings.DefaultPath);

            ShowChosen(settings);

            if (Resolvers.ItemsSource is IEnumerable<ResolverRow> rows)
            {
                foreach (var row in rows)
                    row.Chosen = row.Address == address;

                Redraw();
            }

            Status.Text = $"Апстрим: {address}. Вступит в силу после следующего запуска движков.";
            this.Offer($"Резолвер сменён на {address}");
        }
        catch (Exception ex)
        {
            Status.Text = "Выбор не записался: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Проверяет введённый резолвер обзором и, если он ответил, записывает.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Меряется до записи, а не после: резолвер, не ответивший ничем, в списке
    /// выглядел бы исправным, пока его не выберут и туннель не встанет.
    /// Ответил хоть чем-то — пишется, с тем, что показал замер.
    /// </para>
    /// <para>
    /// Подменяющий по UDP не отвергается: подмену делает оператор по дороге,
    /// а не сам резолвер, и по DoH он может отвечать честно. Но сказано
    /// об этом прямо.
    /// </para>
    /// </remarks>
    private async void OnAddOwn(object sender, RoutedEventArgs e)
    {
        var (provider, problem) = CustomDns.Build(
            OwnName.Text, OwnAddress.Text, OwnDoh.Text, dohPath: null, OwnSecondary.Text);

        OwnStatus.Visibility = Visibility.Visible;

        if (provider is null)
        {
            OwnStatus.Text = problem;
            return;
        }

        OwnAddButton.IsEnabled = false;
        OwnStatus.Text = $"Проверяю {provider.Name}: UDP{(provider.TlsName is null ? string.Empty : ", DoT и DoH")}…";

        try
        {
            var row = (await DnsSurvey.SurveyAllAsync([provider])).Single();

            bool doh = row.DohMs is not null;
            bool udp = row.UdpMs is not null;

            if (!doh && !udp && row.DotMs is null)
            {
                OwnStatus.Text = $"{provider.Name} не ответил ничем: UDP — {Why(row.UdpFailure)}"
                    + (provider.TlsName is null ? string.Empty : $", DoH — {Why(row.DohFailure)}")
                    + ". Не добавлен.";
                return;
            }

            CustomDns.Save(provider);

            var parts = new List<string>();

            if (udp)
                parts.Add($"UDP {row.UdpMs:0} мс");

            if (provider.TlsName is not null)
                parts.Add(doh ? $"DoH {row.DohMs:0} мс" : $"DoH не ответил ({Why(row.DohFailure)})");

            var verdict = provider.TlsName is null
                ? "Для туннеля не годится — нет DoH; в обзоре будет."
                : doh
                    ? "Можно выбрать строкой в списке выше."
                    : "В список попал, но DoH не ответил — туннель через него не заработает, пока DoH молчит.";

            if (row.Spoofed > 0)
                verdict += $" По UDP ответы подменены ({row.Spoofed}/{row.SpoofChecked}) — это оператор по дороге; пользуйтесь DoH.";

            OwnStatus.Text = $"Добавлен {provider.Name}: {string.Join(", ", parts)}. {verdict}";

            OwnName.Clear();
            OwnAddress.Clear();
            OwnSecondary.Clear();
            OwnDoh.Clear();

            Reload();
        }
        catch (Exception ex)
        {
            OwnStatus.Text = "Проверить не удалось: " + ex.GetBaseException().Message;
        }
        finally
        {
            OwnAddButton.IsEnabled = true;
        }
    }

    private static string Why(string failure) => failure.Length > 0 ? failure : "без отклика";

    private void OnRemoveOwn(object sender, RoutedEventArgs e)
    {
        // Кнопка внутри строки-кнопки: без этого нажатие дошло бы до строки
        // и выбрало бы убираемый резолвер апстримом.
        e.Handled = true;

        if (sender is not Button { Tag: string udp, DataContext: ResolverRow row } || udp.Length == 0)
            return;

        try
        {
            CustomDns.Remove(udp);

            // Выбранный туннелю — остаётся в настройках адресом без имени,
            // и DoH к нему не поднимется. Сказать, а не молчать.
            var settings = AppSettings.Load(AppSettings.DefaultPath);

            Reload();

            Status.Text = row.Chosen || settings.DnsServer == row.Address
                ? $"Убран {row.Name}. Он был выбран туннелю — выберите другой, иначе DoH не поднимется."
                : $"Убран {row.Name}.";
        }
        catch (Exception ex)
        {
            Status.Text = "Удалить не удалось: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Включает и выключает автовыбор; включённый сразу запускает обзор.
    /// </summary>
    private void OnAutoDns(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            // При DNS роутера выключатель показан выключенным, хотя автовыбор
            // мог сохраниться включённым: щелчок по нему — «включить», а не
            // переворот сохранённого.
            var next = settings with { DnsAuto = settings.UseRouterDns || !settings.DnsAuto, UseRouterDns = false };

            next.Save(AppSettings.DefaultPath);
            ShowChosen(next);

            if (next.DnsAuto)
            {
                Status.Text = "Автоподбор включён, замеряю резолверы…";
                OnSurvey(sender, e);
            }
            else
            {
                Status.Text = "Автовыбор выключен: остаётся " + ChosenName.Text + ".";
            }
        }
        catch (Exception ex)
        {
            Status.Text = "Выбор не записался: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// После обзора, при включённом автовыборе, ставит быстрейший резолвер.
    /// </summary>
    /// <remarks>
    /// Решает библиотека (<see cref="DnsSurvey.Fastest"/>): окно только
    /// записывает выбор и отмечает строку. Тот же — не выбор: без смены
    /// не зовём и перезапуск.
    /// </remarks>
    private void PickFastest(IReadOnlyList<DnsSurveyRow> all, IReadOnlyList<ResolverRow> choices)
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        if (!settings.DnsAuto || settings.UseRouterDns)
            return;

        if (DnsSurvey.Fastest(all) is not { TlsAddress: { } address } best)
        {
            Status.Text = "Автоподбор: ни один резолвер для туннеля не ответил по DoH, выбор прежний.";
            return;
        }

        if (settings.DnsServer == address)
        {
            Status.Text = $"Автоподбор: самый быстрый уже выбран — {best.Name}.";
            return;
        }

        var next = settings with { DnsServer = address };
        next.Save(AppSettings.DefaultPath);

        foreach (var row in choices)
            row.Chosen = row.Address == address;

        Redraw();
        ShowChosen(next);

        Status.Text = $"Автоподбор: {best.Name} · {address}. Вступит в силу после следующего запуска движков.";
        this.Offer($"Резолвер сменён на {best.Name}");
    }

    /// <summary>
    /// Идёт заполнение списка, а не выбор человека.
    /// </summary>
    /// <remarks>
    /// Список пишет выбор привязкой и поднимает событие при каждом показе
    /// вкладки. Без этой заслонки заход на вкладку записывал бы настройку
    /// обратно и звал уведомление о перезапуске — на ровном месте.
    /// </remarks>
    private bool _filling;

    private void OnThroughTunnel(object sender, RoutedEventArgs e)
    {
        // IsInitialized — от события, которое список поднимает прямо
        // при разборе разметки: соседние элементы к тому мгновению ещё
        // не созданы. На вкладке маршрутов это стоило падения при
        // создании вкладки, и повторять его здесь незачем.
        if (!IsInitialized || _filling || AppSettings.Load().UseRouterDns || sender is not ComboBox box || box.SelectedIndex < 0)
            return;

        try
        {
            if (box.SelectedItem is not ComboBoxItem { Tag: string tag } || !Enum.TryParse<DnsRoute>(tag, out var route))
                return;

            var settings = AppSettings.Load(AppSettings.DefaultPath);

            // Выбор того же самого — не выбор.
            if (settings.DnsVia == route)
                return;

            var next = settings with { DnsVia = route };

            next.Save(AppSettings.DefaultPath);

            ShowChosen(next);

            Status.Text = (next.NeedsDnsEngine
                ? "Имена будет разрешать движок без выхода, по DoH."
                : route switch
                {
                    DnsRoute.Tunnel => "Имена будут разрешаться внутри туннеля.",
                    DnsRoute.Direct => "Имена будут разрешаться напрямую.",
                    _ => "Сначала напрямую, при отказе DoH — по TCP к тому же резолверу, затем через туннель.",
                }) + " Вступит в силу после следующего запуска движков.";

            this.Offer($"DNS: {DnsRoutes.Word(route)}");
        }
        catch (Exception ex)
        {
            Status.Text = "Выбор не записался: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Обзор резолверов — кто отвечает на самом деле и подменяет ли.
    /// </summary>
    /// <remarks>
    /// Строки появляются по мере готовности: провайдеров пятнадцать, и таблица,
    /// молчащая до последнего, выглядела бы зависшей.
    /// </remarks>
    private async void OnSurvey(object sender, RoutedEventArgs e)
    {
        SurveyButton.IsEnabled = false;
        SurveyHead.Visibility = Visibility.Visible;

        _work?.Cancel();
        _work = new CancellationTokenSource();

        var rows = new System.Collections.ObjectModel.ObservableCollection<SurveyRow>();
        var done = new List<DnsSurveyRow>();
        Survey.ItemsSource = rows;
        SurveyStatus.Text = "Проверяю… Запросы идут мимо туннеля, через адаптер.";

        var choices = Resolvers.ItemsSource as IReadOnlyList<ResolverRow> ?? [];

        foreach (var choice in choices)
        {
            choice.Latency = "…";
            choice.Key = "Faint";
        }

        Redraw();

        try
        {
            var progress = new Progress<DnsSurveyRow>(row =>
            {
                rows.Add(SurveyRow.From(row));
                done.Add(row);
                Fill(choices, row);
                Redraw();
            });

            var all = await DnsSurvey.SurveyAllAsync(progress: progress, cancellationToken: _work.Token);

            // Порядок — как в списке провайдеров, а не как пришли ответы.
            Survey.ItemsSource = all.Select(SurveyRow.From).ToList();

            PickFastest(all, choices);

            var intercepted = all.Where(r => r.Spoofed > 0).Select(r => r.Provider.Name).ToList();

            SurveyStatus.Text = intercepted.Count == 0
                ? "Подмены по UDP нет."
                : $"По UDP подменяют ответы: {string.Join(", ", intercepted)}. "
                  + "Обычный DNS к ним перехвачен по дороге — пользуйтесь DoH или DoT.";

            _last = new LastSurvey(all, SurveyStatus.Text, DateTime.Now);
        }
        catch (OperationCanceledException)
        {
            SurveyStatus.Text = "Сводка прервана.";

            // Прерван уходом с вкладки — что успели, то и запомнили.
            if (done.Count > 0)
                _last = new LastSurvey(done.ToList(), $"Обзор прерван: проверено {done.Count} из {DnsSurvey.All.Count}.", DateTime.Now);
        }
        catch (Exception ex)
        {
            SurveyStatus.Text = "Сводку получить не удалось: " + ex.GetBaseException().Message;
        }
        finally
        {
            SurveyButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Проверяет адреса пинов и меняет умершие на те, что посредники отдают сейчас.
    /// </summary>
    /// <remarks>
    /// Не прерывается уходом с вкладки: посреди неё может идти запись hosts,
    /// а итог всё равно ляжет в журнал. Токен поэтому не раздела, а пустой.
    /// </remarks>
    private async void OnPins(object sender, RoutedEventArgs e)
    {
        PinsButton.IsEnabled = false;

        try
        {
            var state = SupervisorState.Load(SupervisorState.DefaultPath);
            bool engines = state is not null && state.IsSupervisorAlive();

            PinsStatus.Text = "Проверяю: по одному соединению на первый адрес каждого имени и вопрос посредникам…";

            PinRefreshResult result;

            try
            {
                result = await Task.Run(() => PinRefresh.RunAsync(CancellationToken.None, byHand: true));
            }
            catch (Exception ex)
            {
                PinsStatus.Text = "Проверить не удалось: " + ex.GetBaseException().Message;
                Journal.Write("пин", "проверка адресов не удалась: " + ex.GetBaseException().Message);
                return;
            }

            PinsStatus.Text = Describe(result)
                + (engines ? string.Empty : " Движки не подняты — часть посредников без десинка не отвечает, и мёртвыми могли показаться живые.");

            // Проверка прошла; сбой записи в журнал наблюдения её не отменяет.
            try
            {
                PinRefreshWatch.Write("кнопкой", result);
                ShowPinWatch(AppSettings.Load(AppSettings.DefaultPath));
            }
            catch (Exception ex)
            {
                PinsStatus.Text += " Запись о проверке не сохранилась: " + ex.GetBaseException().Message;
            }
        }
        catch (Exception ex)
        {
            PinsStatus.Text = "Проверить не удалось: " + ex.GetBaseException().Message;
        }
        finally
        {
            PinsButton.IsEnabled = true;
        }
    }

    /// <summary>Выключатель плановой проверки; сторож читает его на каждом круге.</summary>
    private void OnAutoRepin(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath) with { AutoRepin = AutoRepinSwitch.IsChecked == true };

            settings.Save(AppSettings.DefaultPath);
            ShowPinWatch(settings);
        }
        catch (Exception ex)
        {
            PinsStatus.Text = "Не удалось сохранить настройку: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>Выключатель и строка о том, что под присмотром и когда была проверка.</summary>
    private void ShowPinWatch(AppSettings settings)
    {
        AutoRepinSwitch.IsChecked = settings.AutoRepin;

        var watch = PinRefresh.Watch();
        var parts = new List<string>();

        if (watch.Watched > 0)
            parts.Add("под присмотром: " + string.Join(", ", watch.Groups.Select(g => $"{g.Intermediary ?? "без посредника"} — {g.Count}")));

        parts.Add(watch.Last is { } last
            ? $"последняя проверка {last.ToLocalTime():dd.MM HH:mm}"
            : "проверок ещё не было");

        if (watch.Suspects.Count > 0)
            parts.Add($"промолчали в прошлый раз: {watch.Suspects.Count}");

        PinsWatchLine.Text = char.ToUpper(parts[0][0]) + parts[0][1..] + (parts.Count > 1 ? "; " + string.Join("; ", parts.Skip(1)) : string.Empty) + ".";
    }

    /// <summary>Итог проверки словами — что сделано и чего не сделано.</summary>
    internal static string Describe(PinRefreshResult result)
    {
        if (result.Checked == 0 && result.Error is null)
            return "В hosts нет пинов на адреса посредников — проверять нечего.";

        var parts = new List<string>();

        if (result.Error is { } error)
            parts.Add($"Ничего не заменено: {error}.");

        if (result.Changes.Count > 0)
            parts.Add($"Заменено: {string.Join("; ", result.Changes)}.");

        if (result.Moved.Count > 0)
            parts.Add($"Перенесено за посредником: {string.Join("; ", result.Moved)}.");

        if (result.Reordered.Count > 0)
            parts.Add($"Живой адрес поставлен первым: {string.Join("; ", result.Reordered)}.");

        if (result.Dead.Count > 0)
            parts.Add($"Живой замены не нашлось: {string.Join(", ", result.Dead.Take(6))}"
                + (result.Dead.Count > 6 ? $" и ещё {result.Dead.Count - 6}." : "."));

        if (result.Silent.Count > 0)
            parts.Add($"По DoH не ответили: {string.Join(", ", result.Silent)}.");

        if (parts.Count == 0)
            parts.Add($"Проверено адресов: {result.Checked}, все отвечают, менять нечего.");

        return string.Join(" ", parts);
    }
}
