using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Zephyron.Core;
using Zephyron.Proxy;

namespace Zephyron.Supervisor;

/// <summary>Что получилось: где лежит архив и что в него вошло.</summary>
public sealed record SupportReportResult(string Path, IReadOnlyList<string> Files);

/// <summary>
/// Отчёт для разбора: журналы и настройки одним архивом, без ссылок и ключей.
/// </summary>
/// <remarks>
/// <para>
/// 24.09 жалоба «подписка не прочиталась: Этот хост неизвестен» упёрлась
/// в то, что разбирать было нечем: пришлось писать человеку, что где
/// посмотреть, и ждать. Одним файлом он отвечает сразу — и тем, что нужно,
/// а не тем, что он догадался скопировать.
/// </para>
/// <para>
/// Ссылка на подписку равносильна паролю, и человек, отправляющий отчёт
/// в общий чат, её не заметит. Поэтому вычистка — не вежливость, а условие:
/// ссылки подписок убираются дословно, прочие ссылки теряют путь, ссылки
/// прокси и ключи — целиком (<see cref="Redact"/>). Файлы с ключами —
/// singbox.json, subscriptions.json, warp.json — не берутся вовсе.
/// </para>
/// <para>
/// Журналы — начало и конец, а не хвост: первая ошибка падает по своей
/// причине, остальные — по её следам (AGENTS.md), и в хвосте она не видна.
/// </para>
/// </remarks>
public static class SupportReport
{
    /// <summary>Куда кладётся архив: рядом с отчётами проверки блокировок.</summary>
    public static string DefaultDirectory => "reports";

    /// <summary>Сколько строк журнала брать с начала и с конца.</summary>
    private const int HeadLines = 400;
    private const int TailLines = 600;

    /// <summary>Собирает архив.</summary>
    /// <param name="version">Версия с номером сборки — как в заголовке окна.</param>
    /// <param name="secrets">
    /// Что ещё спрятать дословно. Ссылки подписок из настроек и книги
    /// подписок отчёт находит сам — забыть их передать нельзя.
    /// </param>
    /// <param name="root">Корень установки; <c>null</c> — рабочий каталог (окно уходит в корень при запуске).</param>
    /// <param name="machine">
    /// Читать ли саму машину, а не только корень установки: сеть и чужие
    /// обходы, проверки «Диагностики», журнал Discord, hosts, работающий движок.
    /// Тестам — выключено: они собирают отчёт из выдуманного корня, а это
    /// читалось бы с машины, на которой их гоняют.
    /// </param>
    public static SupportReportResult Create(
        string version,
        IEnumerable<string?>? secrets = null,
        string? directory = null,
        string? root = null,
        DateTime? now = null,
        bool machine = false)
    {
        var when = now ?? DateTime.Now;
        var settings = AppSettings.Load(At(root, AppSettings.DefaultPath));
        var state = SupervisorState.Load(At(root, SupervisorState.DefaultPath));

        var book = BookUrls(At(root, Path.Combine("config", "subscriptions.json")));

        // Последняя проверка блокировок: для разбора «не открывается» — первое,
        // что хочется увидеть (владелец 26.09 спросил, входит ли она). Одна,
        // самая свежая: прошлые описывают сеть, которой уже нет.
        var blockcheck = LatestBlockcheck(At(root, DefaultDirectory));

        // Замеры скорости (владелец 30.09: «есть ли последние результаты проверки
        // в отчёте?»). Адреса в них нет — история его не хранит.
        var speed = SpeedHistory.Load(At(root, SpeedHistory.DefaultPath));

        var hidden = (secrets ?? [])
            .Append(settings.SubscriptionUrl)
            .Concat(book)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var parts = new List<(string Name, string Text)>
        {
            // Имена файлов — латиницей. Русские в архиве у части распаковщиков
            // выходят кракозябрами — так показал unzip из Git 25.09. Отчёт уходит
            // к чужим людям с чужими распаковщиками.
            ("summary.txt", Summary(version, settings, state, book.Count, blockcheck, speed, when)
                + (machine ? Live(settings, state, root) : string.Empty)),

            // Ссылку из настроек убираем до сериализации, а не надеемся
            // на вычистку: поле ради того и названо.
            ("settings.json", JsonSerializer.Serialize(
                settings with { SubscriptionUrl = null, TelegramProxySecret = null },
                new JsonSerializerOptions { WriteIndented = true })),
        };

        // Что вокруг: чужие обходы, VPN-клиенты, адаптеры, DNS. Без этого
        // три отчёта подряд 28.09 кончались просьбой прислать вывод команд.
        if (machine)
        {
            var ours = (state?.Services ?? [])
                .Where(s => s.ProcessId is not null)
                .Select(s => s.ProcessId!.Value)
                .ToHashSet();

            parts.Add(("network.txt", NetworkSnapshot.Describe(ours)));

            // Проверки «Диагностики» — те же, что человек видит в окне. №18
            // (05.10): безопасный DNS Chrome и служба Flowseal были жёлтыми
            // у него на экране, а в отчёт не попадали — пересказывал словами.
            // Права не проверяются: отчёт собирает и zy без администратора.
            parts.Add(("doctor.txt", Section(() => Doctor.Describe(Doctor.Run(settings, elevation: false)))));

            // Голос Discord: что сторож дописал бы — тот же текст, что zy voice.
            parts.Add(("voice.txt", Section(() => Core.Services.DiscordVoiceLearn.Report(root: root).Text)));

            // hosts целиком решает, куда уходит имя и трогает ли его десинк.
            // №18: Instagram был прибит к адресам Meta, а узнали об этом
            // из вложения, которое человек догадался прислать сам.
            if (Read(HostsFile.DefaultPath) is { } hosts)
                parts.Add(("hosts.txt", HostsExcerpt(hosts)));

            // Особые маршруты против hosts: на каком пине держится маршрут и стоит ли он.
            parts.Add(("special-routes.txt", Section(() => SpecialRoutes())));
        }

        // Сторож серверов и недавние выходы: что отвечало и когда — для жалоб
        // «туннель отваливается», где журнал движка показывает только ошибки.
        if (Servers(root) is { } servers)
            parts.Add(("servers.txt", servers));

        if (Section(() => SubscriptionServers(root)) is { Length: > 0 } listed)
            parts.Add(("subscription-servers.txt", listed));

        // Журналы сменяются по размеру (RollingLog, 4 МБ), а не по запуску:
        // winws2 пишет строку на соединение, и начало запуска с его первой
        // ошибкой к сбору отчёта нередко уже в прошлом поколении.
        foreach (var log in new[] { "supervisor.log", "sing-box.log", "winws2.log", "tg-ws-proxy.log" })
        {
            AddLog(parts, log, At(root, Path.Combine("runtime", log)));
            AddLog(parts, Path.GetFileNameWithoutExtension(log) + ".1.log", At(root, Path.Combine("runtime", log + ".1")));
        }
        // Наблюдение (владелец 10.10: «и отдельно по программам, и отдельно
        // полностью журнал»). Сводка — всегда, и без журнала: «не включали»
        // тоже ответ, а пустое место читалось бы как «не попало в отчёт».
        // Журнал — целиком, не выдержкой: соединение ищут по времени жалобы,
        // и оно может быть в любом месте файла.
        var watch = At(root, ConnectionWatch.DefaultJournal);
        parts.Add(("watch-summary.txt", Core.Connections.WatchEntry.Summarize(
            (Read(watch + ".1") ?? string.Empty).Split('\n').Concat((Read(watch) ?? string.Empty).Split('\n'))
                .Select(l => l.TrimEnd('\r')))));
        AddFile(parts, "watch.1.log", watch + ".1");
        AddFile(parts, "watch.log", watch);

        AddFile(parts, "rules.user.yaml", At(root, Path.Combine("config", "rules.user.yaml")));
        AddFile(parts, "desync-exclude.txt", At(root, Path.Combine("runtime", "desync-exclude.txt")));
        AddFile(parts, "desync-keep.txt", At(root, Path.Combine("runtime", "desync-keep.txt")));

        // Сводка ошибок по всем поколениям журналов: выдержка выше теряет середину,
        // а сырые 90 таймаутов без пояснения разбирать вручную (владелец 15.11).
        parts.Insert(1, ("errors.txt", Section(() => ErrorDigest(
            new[] { "sing-box.log", "tg-ws-proxy.log", "winws2.log", "supervisor.log" }
                .Select(log => (log, (Read(At(root, Path.Combine("runtime", log + ".1"))) ?? string.Empty)
                    + "\n" + (Read(At(root, Path.Combine("runtime", log))) ?? string.Empty)))))));

        // Что автоподбор записал в маршруты и hosts — без него не понять, откуда правило или пин.
        AddFile(parts, "autopick-journal.json", At(root, AutoPickJournal.DefaultPath));

        // И почему: итог последнего прогона с пробами по каждому имени.
        if (LatestAutoPick(At(root, DefaultDirectory)) is { } autopick)
            AddFile(parts, "autopick-last.txt", autopick.FullName);

        // Сбои окна (App.Show): исключение со стеком — в остальных журналах их нет.
        AddFile(parts, "gui.log", At(root, Path.Combine("logs", "gui.log")));

        // Журналы подмены: что robocopy не смог заменить и почему (жалоба 06.10).
        AddFile(parts, "update.log", At(root, Core.Updates.UpdateInstaller.LogFile));
        AddFile(parts, "update-failed.log", At(root, Core.Updates.UpdateInstaller.FailedLogFile));

        if (blockcheck is not null)
            AddFile(parts, blockcheck.Name, blockcheck.FullName);

        if (speed.Count > 0)
            parts.Add(("speed.txt", SpeedHistory.Describe(speed)));

        var folder = directory ?? At(root, DefaultDirectory);
        Directory.CreateDirectory(folder);

        var path = Path.GetFullPath(Path.Combine(folder, $"zephyron-report-{when:yyyyMMdd-HHmmss}.zip"));

        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (name, text) in parts)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);

                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(Redact(text, hidden));
            }
        }

        return new SupportReportResult(path, parts.Select(p => p.Name).ToList());
    }

    private static string Summary(
        string version,
        AppSettings settings,
        SupervisorState? state,
        int subscriptions,
        FileInfo? blockcheck,
        IReadOnlyList<SpeedEntry> speed,
        DateTime when)
    {
        var text = new StringBuilder();

        text.AppendLine($"Zephyron {version}");
        text.AppendLine($"Снят: {when:dd.MM.yyyy HH:mm:ss}");
        text.AppendLine($"Windows: {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({Environment.OSVersion.Version}), "
            + $"{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}, .NET {Environment.Version}");
        text.AppendLine();
        text.AppendLine($"Режим: {settings.Engines.Describe()}");
        text.AppendLine($"Игнорировать исключения: {YesNo(settings.Engines.IgnoreExclusions)}");
        text.AppendLine($"Пресет: {settings.PresetName ?? "не выбран"}");
        text.AppendLine($"Перехват: {Zephyron.Zapret.PresetCapture.Word(settings.Capture)}; game filter: {YesNo(settings.GameFilter)}");
        text.AppendLine((settings.UseRouterDns ? $"DNS: роутер {settings.RouterDnsAddress}:53, UDP/LAN"
            : settings.DnsCryptActive is { } crypt ? $"DNS: {DnsCrypt.Describe(crypt).Title} (dnscrypt-proxy)"
            : $"DNS: {settings.DnsServer}, {DnsRoutes.Word(settings.DnsVia)}")
            + (settings.NeedsDnsEngine ? " (туннеля нет — отвечает движок без выхода)" : string.Empty));
        text.AppendLine($"WARP: {YesNo(settings.WarpEnabled)}");
        text.AppendLine($"Подписок: {subscriptions} (ссылки в отчёт не входят)");
        text.AppendLine($"Проверка прохода трафика: {YesNo(settings.VerifyTraffic)}");
        text.AppendLine(blockcheck is null
            ? "Проверка блокировок: не проводилась"
            : $"Проверка блокировок: {blockcheck.Name}, снята {blockcheck.LastWriteTime:dd.MM.yyyy HH:mm}");

        // Свежий замер каждого пути — в сводке, остальные — в speed.txt.
        if (speed.Count == 0)
        {
            text.AppendLine("Замер скорости: не проводился");
        }
        else
        {
            foreach (var entry in new[] { speed.FirstOrDefault(e => e.Tunnel), speed.FirstOrDefault(e => !e.Tunnel) }
                .OfType<SpeedEntry>()
                .OrderByDescending(e => e.At))
            {
                text.AppendLine($"Замер скорости {(entry.Tunnel ? "через туннель" : "напрямую")}, "
                    + $"{entry.At.ToLocalTime():dd.MM.yyyy HH:mm}: {SpeedVerdict.Line(entry)}");
            }
        }

        text.AppendLine();

        if (state is null)
        {
            text.AppendLine("Надзор: движки не поднимались");
        }
        else
        {
            text.AppendLine($"Надзор: {(state.IsSupervisorAlive() ? "жив" : "не отвечает")}");

            foreach (var service in state.Services)
            {
                text.Append($"  {service.Name}: {service.Health}");

                if (service.RestartCount > 0)
                    text.Append($", перезапусков {service.RestartCount}");

                if (service.StartedAt is { } started)
                    text.Append($", поднят {started.ToLocalTime():dd.MM HH:mm:ss}");

                text.AppendLine();

                if (!string.IsNullOrWhiteSpace(service.LastError))
                    text.AppendLine($"    последняя ошибка: {service.LastError}");

                if (!string.IsNullOrWhiteSpace(service.Remark))
                    text.AppendLine($"    примечание: {service.Remark}");
            }
        }

        return text.ToString();
    }

    private static string YesNo(bool value) => value ? "да" : "нет";

    /// <summary>
    /// Серверы из сохранённых подписок: номер, протокол, транспорт и почему
    /// отброшен. Без адресов, ключей и паролей — их человек выкладывает в обсуждения.
    /// </summary>
    internal static string SubscriptionServers(string? root)
    {
        var folder = At(root, Subscriptions.SubscriptionPool.DefaultReserveDirectory);

        if (!Directory.Exists(folder))
            return string.Empty;

        var text = new StringBuilder();
        int n = 0;

        foreach (var file in Directory.GetFiles(folder, "*.txt").OrderBy(f => f, StringComparer.Ordinal))
        {
            var (servers, errors) = Subscriptions.SubscriptionParser.ParseBody(File.ReadAllText(file));
            int skipped = servers.Count(s => !s.IsUsableOutbound);

            text.AppendLine($"Подписка {++n}: серверов {servers.Count}, отброшено {skipped}, не разобрано строк {errors.Count}");

            // Номером, а не именем: имя сервера — часто «хост:порт» или домен поставщика.
            int i = 0;
            foreach (var s in servers)
                text.AppendLine($"  {(s.IsUsableOutbound ? "+" : "−")} сервер {++i} — {s.ProtocolName} {s.Transport}/{s.Security}"
                    + (s.UnusableWhy is { } reason ? $": {reason}" : string.Empty));

            // Служебные строки панели и признаки пустышки — без адресов и названий.
            var verdict = Subscriptions.SubscriptionTrust.Assess(new() { Servers = servers }, DateTimeOffset.Now);
            text.AppendLine($"  доверие: {verdict.Level}" + (verdict.Reasons.Count > 0 ? "; " + string.Join("; ", verdict.Reasons) : string.Empty));

            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>Часть отчёта, которая может не собраться: тогда — почему, а не пропуск архива.</summary>
    private static string Section(Func<string> build)
    {
        try
        {
            return build();
        }
        catch (Exception ex)
        {
            return "не собралось: " + ex.GetBaseException().Message + Environment.NewLine;
        }
    }

    /// <summary>
    /// Что сейчас у работающих движков и чем они запущены — дополнение к сводке.
    /// </summary>
    /// <remarks>
    /// Выход — у самого движка, а не из настроек: там «авто», а движок мог
    /// держаться другого (так же, как zy status). DNS — по конфигу работающего
    /// движка (zy dns-mode): 30.09 имена мимо VPN разрешались через туннель.
    /// Отпечатки движков — потому что их подменяют: в №18 человек положил
    /// рядом winws.exe и WinDivert.dll от Flowseal «на авось».
    /// </remarks>
    private static string Live(AppSettings settings, SupervisorState? state, string? root)
    {
        var text = new StringBuilder();

        text.AppendLine();
        text.AppendLine($"Установка: {Path.GetFullPath(At(root, "."))}");

        if (state is not null && state.IsSupervisorAlive())
        {
            if (state.Services.FirstOrDefault(s => s.Name == "sing-box") is { } singBoxService)
            {
                var remark = singBoxService.Remark;

                var exit = Section(() =>
                {
                    var (server, automatic) = TunnelStatus.CurrentExitAsync(CancellationToken.None).GetAwaiter().GetResult();

                    return server is null
                        ? "движок не ответил"
                        : $"{server} ({TunnelStatus.StandingWord(server, automatic, settings.PreferredServer, remark)})";
                });

                text.AppendLine($"Выход сейчас: {exit.Trim()}");
            }

            if (state.EngineAnswersDns())
            {
                var dns = Section(() =>
                {
                    var config = System.Text.Json.Nodes.JsonNode.Parse(
                        File.ReadAllText(At(root, EngineKeys.DefaultConfigPath)));

                    var final = (string?)config?["dns"]?["final"] ?? "?";

                    return DnsPath.Describe(config?["dns"], final, string.Empty);
                });

                text.AppendLine($"DNS движка: {dns.Trim()}");
            }
        }

        text.AppendLine($"WARP у провайдера, похоже, недоступен: {YesNo(WarpReach.LooksBlocked(At(root, ServerHealthCache.DefaultPath)))}");

        var winws = Zephyron.Zapret.ZapretPaths.Discover()?.ExecutablePath;
        var singBox = Path.Combine(AppContext.BaseDirectory, "engines", "sing-box", "sing-box.exe");

        text.AppendLine($"winws2.exe: {Fingerprint(winws)}");
        text.AppendLine($"sing-box.exe: {Fingerprint(singBox)}");

        return text.ToString();
    }

    /// <summary>Размер и начало SHA-256 — сверить с архивом выпуска.</summary>
    private static string Fingerprint(string? path)
    {
        try
        {
            if (path is null || !File.Exists(path))
                return "нет";

            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));

            return $"{new FileInfo(path).Length} байт, SHA-256 {hash[..16].ToLowerInvariant()}…";
        }
        catch (Exception ex)
        {
            return "не читается: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Сторож серверов и недавние выходы; <c>null</c> — нет ни того, ни другого.
    /// </summary>
    /// <remarks>
    /// В файлах только имена серверов, задержки и время — ни адресов, ни ключей.
    /// </remarks>
    private static string? Servers(string? root)
    {
        var health = ServerHealthCache.Load(At(root, ServerHealthCache.DefaultPath)).Entries.Values
            .OrderByDescending(h => h.CheckedAt)
            .ToList();

        var exits = ExitHistory.Read(At(root, ExitHistory.DefaultPath))
            .OrderByDescending(e => e.At)
            .ToList();

        if (health.Count == 0 && exits.Count == 0)
            return null;

        var text = new StringBuilder();

        text.AppendLine("Проверки серверов (сторож), свежие первыми; последние исходы — от старого к новому, + ответил, − нет");

        foreach (var h in health)
        {
            text.Append($"  {h.Tag}: {(h.Success ? $"ответил{(h.LatencyMs is { } ms ? $" за {ms:0} мс" : string.Empty)}" : "не ответил")}, "
                + $"{h.CheckedAt.ToLocalTime():dd.MM HH:mm:ss}; {string.Concat(h.Recent.Select(ok => ok ? '+' : '−'))}");

            if (h.Failures > 0)
                text.Append($"; промахов подряд {h.Failures}");

            // Flaky сторожа берёт и молчащие совсем — для отбора это одно,
            // а читающему «мигающий» у сервера без единого ответа соврёт.
            if (h.Flaky && h.Recent.Any(ok => ok))
                text.Append("; мигающий");

            text.AppendLine();
        }

        if (health.Count == 0)
            text.AppendLine("  не проверялись");

        text.AppendLine();
        text.AppendLine("Недавние выходы туннеля, свежие первыми");

        foreach (var exit in exits)
            text.AppendLine($"  {exit.At.ToLocalTime():dd.MM HH:mm:ss}  {exit.Tag}");

        if (exits.Count == 0)
            text.AppendLine("  не записывались");

        return text.ToString();
    }

    /// <summary>
    /// Строки hosts с адресами и границы нашего блока — без пояснений Microsoft.
    /// </summary>
    /// <summary>Особые маршруты и их пины — сверенные с нынешним hosts.</summary>
    internal static string SpecialRoutes(string? rulesPath = null, string? hostsPath = null)
    {
        var marked = Core.Rules.UserRulesFile.Load(rulesPath ?? Core.Rules.UserRulesFile.DefaultPath).Entries
            .Where(e => e.Special is not null)
            .ToList();

        if (marked.Count == 0)
            return "Особых маршрутов нет.";

        var pinned = HostsFile.Read(hostsPath);
        var text = new StringBuilder();

        foreach (var entry in marked)
        {
            text.AppendLine(Core.Rules.RulesShare.Describe(entry));

            foreach (var (name, address) in entry.PinHosts ?? new Dictionary<string, string>())
            {
                var state = !pinned.TryGetValue(name, out var list) ? "НЕТ в hosts"
                    : list.Any(a => a.ToString() == address) ? "стоит"
                    : $"в hosts другой адрес: {string.Join(", ", list)}";

                text.AppendLine($"  {name} → {address}: {state}");
            }
        }

        return text.ToString();
    }

    public static string HostsExcerpt(string hosts)
    {
        var lines = hosts.Replace("\r", string.Empty).Split('\n');

        var kept = lines
            .Where(line => line.Trim().Length > 0)
            .Where(line => !line.TrimStart().StartsWith('#')
                || line.Contains(HostsEditor.BlockBegin, StringComparison.Ordinal)
                || line.Contains(HostsEditor.BlockEnd, StringComparison.Ordinal))
            .ToList();

        int pins = kept.Count(line => !line.TrimStart().StartsWith('#'));

        return $"Строк с адресами: {pins}. Блок Zephyron — между отметками {HostsEditor.BlockBegin} и {HostsEditor.BlockEnd}; "
            + $"«# {HostsEditor.DesyncMark}» — пин на адрес самого сервиса, десинк к нему применяется; "
            + $"«{HostsEditor.ViaMark}…» — каким посредником отдан адрес, у него автозамена о пине и спрашивает.\n\n"
            + string.Join('\n', kept) + "\n";
    }

    /// <summary>Самый свежий отчёт проверки блокировок; <c>null</c> — проверок не было.</summary>
    private static FileInfo? LatestAutoPick(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                ? new DirectoryInfo(folder).GetFiles("autopick-*.txt").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static readonly Regex Noise = new(
        @"^\S+\s+(?=\S*(ERROR|WARN|FATAL))|\[\d+\s[\d.]+[mµ]?s\]|\b\d{4}-\d\d-\d\dT\S+|\[\d{4}\]|\[\d\d:\d\d:\d\d\]|\d{4}-\d\d-\d\d \d\d:\d\d:\d\d",
        RegexOptions.Compiled);

    private static readonly Regex Endpoint = new(@"\[?[0-9a-f:.]*[0-9a-f]\]?:\d{2,5}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Digits = new(@"\d+", RegexOptions.Compiled);

    /// <summary>
    /// Ошибки и предупреждения журналов, сведённые по виду: сколько, когда последняя,
    /// куда чаще всего и что это значит простыми словами.
    /// </summary>
    internal static string ErrorDigest(IEnumerable<(string Source, string Text)> logs)
    {
        var text = new StringBuilder();
        text.AppendLine("Ошибки и предупреждения журналов по видам (все поколения журнала, не выдержка).");
        text.AppendLine("«пример» — последняя такая запись; «куда» — самые частые адреса и имена; «когда» — первая и последняя, если журнал пишет дату.");

        foreach (var (source, raw) in logs)
        {
            var groups = new Dictionary<string, Group>();

            foreach (var line in raw.Split('\n'))
            {
                var clean = Spaces.Replace(Ansi.Replace(line, string.Empty).Replace("\0", string.Empty), " ").Trim();

                if (clean.Length == 0 || !IsProblem(clean))
                    continue;

                var body = Noise.Replace(clean, string.Empty).Trim();
                var key = Digits.Replace(Host.Replace(Endpoint.Replace(body, "АДРЕС"), "ИМЯ"), "N");

                // Куда: внешний адрес или имя; свои (TUN, петля, подменные) ничего не говорят.
                var where = Endpoint.Matches(body).Select(m => m.Value.Trim('[', ']')).FirstOrDefault(v => !IsLocal(v))
                    ?? (Host.Match(body) is { Success: true } h ? h.Value : null);

                if (!groups.TryGetValue(key, out var group))
                    groups[key] = group = new Group();

                group.Count++;
                group.Last = clean;

                if (Stamp.Match(clean) is { Success: true } stamp)
                {
                    group.First ??= stamp.Value;
                    group.LastStamp = stamp.Value;
                }

                if (where is not null)
                    group.Where[where] = group.Where.GetValueOrDefault(where) + 1;
            }

            text.AppendLine().AppendLine($"== {source}: {(groups.Count == 0 ? "ошибок нет" : $"видов {groups.Count}, записей {groups.Values.Sum(v => v.Count)}")}");

            foreach (var group in groups.Values.OrderByDescending(g => g.Count).Take(15))
            {
                text.AppendLine().AppendLine($"[{group.Count}] {Explain(source, group.Last)}");

                if (group.First is not null)
                    text.AppendLine($"  когда: {group.First} … {group.LastStamp}");

                text.AppendLine("  пример: " + (group.Last.Length > 260 ? group.Last[..260] + "…" : group.Last));

                if (group.Where.Count > 0)
                    text.AppendLine("  куда: " + string.Join(", ", group.Where.OrderByDescending(w => w.Value).Take(5).Select(w => $"{w.Key} ×{w.Value}")));
            }
        }

        return text.ToString();
    }

    private sealed class Group
    {
        public int Count;
        public string Last = string.Empty;
        public string? First;
        public string? LastStamp;
        public readonly Dictionary<string, int> Where = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly Regex Spaces = new(@"\s{2,}", RegexOptions.Compiled);

    private static readonly Regex Host = new(@"\b(?:[a-z0-9-]+\.)+[a-z]{2,}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Stamp = new(@"\d{4}-\d\d-\d\d[T ]\d\d:\d\d(:\d\d)?", RegexOptions.Compiled);

    private static bool IsLocal(string endpoint) =>
        endpoint.StartsWith("127.", StringComparison.Ordinal) || endpoint.StartsWith("172.19.", StringComparison.Ordinal)
        || endpoint.StartsWith("198.18.", StringComparison.Ordinal) || endpoint.StartsWith("198.19.", StringComparison.Ordinal)
        || endpoint.StartsWith("fdfe:", StringComparison.OrdinalIgnoreCase) || endpoint.StartsWith("fc00:", StringComparison.OrdinalIgnoreCase)
        || endpoint.StartsWith("::1", StringComparison.Ordinal);

    private static bool IsProblem(string line) =>
        line.Contains("ERROR", StringComparison.Ordinal) || line.Contains("FATAL", StringComparison.Ordinal)
        || line.Contains(" WARN ", StringComparison.Ordinal) || line.StartsWith("WARN", StringComparison.Ordinal)
        || line.Contains("ошибк", StringComparison.OrdinalIgnoreCase) || line.Contains("не удалось", StringComparison.OrdinalIgnoreCase)
        || line.Contains("упал", StringComparison.OrdinalIgnoreCase);

    /// <summary>Что значит запись — по известным признакам; неизвестное так и названо.</summary>
    internal static string Explain(string source, string line)
    {
        bool Has(string word) => line.Contains(word, StringComparison.OrdinalIgnoreCase);
        bool telegram = Has("149.154.") || Has("91.108.") || Has("91.105.") || Has("95.161.") || Has("185.76.151.");

        if (Has("i/o timeout") && Has("outbound/direct"))
            return telegram
                ? "Прямое соединение с сервером Telegram не дождалось ответа: так tg-ws-proxy пробует прямой путь, провайдер его режет, прокси идёт через мосты. Не сбой."
                : "Прямое соединение не дождалось ответа: адрес режет провайдер или он не отвечает. Если это нужный сайт — ему путь «десинк» или «через VPN».";

        if (Has("i/o timeout"))
            return "Соединение через туннель не дождалось ответа: сервер VPN или сайт не ответил вовремя. Много таких подряд — плохой сервер выхода.";

        if (Has("endpoint not connected") || Has("connection reset") || Has("broken pipe") || Has("use of closed") || Has("forcibly closed"))
            return "Соединение закрыла одна из сторон (закрыли вкладку, сервер оборвал). Обычное дело, не сбой.";

        if (Has("tls handshake eof") || Has("handshake") && source.StartsWith("tg-ws", StringComparison.Ordinal))
            return "Прямой путь tg-ws-proxy к Telegram срезан на рукопожатии — прокси уходит на мосты Cloudflare. Не сбой, если мосты отвечают.";

        if (Has("CF WS") && Has("timed out"))
            return "Мост Cloudflare к Telegram не ответил вовремя — прокси пробует следующий. Сбой, только если не отвечают все (тогда «Подобрать мосты» в TG Proxy).";

        if (Has("not in --dc-ip"))
            return "Telegram попросил служебный сервер (обычно медиа или тестовый), для которого у прокси нет прямого адреса — идёт через мосты. Сбой, только если не грузятся фото и видео.";

        if (Has("all configured domains"))
            return "Ни один мост Cloudflare не открылся для этого сервера Telegram — вот это сбой: «Подобрать мосты» в TG Proxy или свой воркер.";

        if (Has("pre-connect"))
            return "Прокси не смог заранее открыть запасное соединение — откроет при первом запросе. Не сбой.";

        if (Has("11001") || Has("11004") || Has("хост неизвестен") || Has("данные запрошенного типа"))
            return "Имя моста не разрешилось через DNS: мост закрыт или DNS не ответил. Прокси берёт следующий; сбой, только если так со всеми.";

        if (Has("10065") || Has("недоступного хоста"))
            return "Адрес был недоступен в этот момент (нет сети или маршрута): обычно после сна или смены сети.";

        if (Has("HTTP 503") || Has("HTTP 502") || Has("HTTP 52"))
            return "Мост или сервер ответил ошибкой на своей стороне — берётся следующий. Сбой, только если так отвечают все.";

        if (Has("certificate") || Has("x509"))
            return "Сертификат сервера не принят: у сервера подписки он просрочен или чужой, либо соединение подменяют.";

        if (Has("no route") || Has("unreachable") || Has("network is down"))
            return "Сети не было в этот момент (сон, смена Wi-Fi, отключение кабеля).";

        if (Has("lookup") || Has("dns") || Has("no such host"))
            return "Имя не разрешилось: DNS не ответил или имени нет.";

        if (Has("refused"))
            return "Сервер отказал в соединении на этом порту: сервис там не работает.";

        return "Без готового пояснения — смотреть пример и сам журнал рядом.";
    }

    private static FileInfo? LatestBlockcheck(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
                return null;

            return new DirectoryInfo(folder)
                .GetFiles("blockcheck-*.txt")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Ссылки из книги подписок — все строки полей <c>Url</c>.
    /// </summary>
    /// <remarks>
    /// Книгу ведёт окно, и её класс живёт там же; zy его не видит. Поэтому
    /// разбирается сам файл, не завися от формата записей: любое поле Url
    /// на любой глубине — ссылка, которую надо спрятать.
    /// </remarks>
    private static List<string> BookUrls(string path)
    {
        var urls = new List<string>();

        if (Read(path) is not { } json)
            return urls;

        try
        {
            using var document = JsonDocument.Parse(json);
            Collect(document.RootElement);
        }
        catch (JsonException)
        {
            // Битая книга — прятать из неё нечего, но и падать незачем.
        }

        return urls;

        void Collect(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        if (property.Name.Equals("Url", StringComparison.OrdinalIgnoreCase)
                            && property.Value.ValueKind == JsonValueKind.String
                            && property.Value.GetString() is { Length: > 0 } url)
                        {
                            urls.Add(url);
                        }
                        else
                        {
                            Collect(property.Value);
                        }
                    }

                    break;

                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        Collect(item);

                    break;
            }
        }
    }

    /// <summary>Путь от корня установки; без корня — от рабочего каталога, как у окна.</summary>
    private static string At(string? root, string relative) =>
        string.IsNullOrEmpty(root) ? relative : Path.Combine(root, relative);

    private static void AddLog(List<(string, string)> parts, string name, string path)
    {
        if (Read(path) is { } text)
            parts.Add((name, Excerpt(text, HeadLines, TailLines)));
    }

    private static void AddFile(List<(string, string)> parts, string name, string path)
    {
        if (Read(path) is { } text)
            parts.Add((name, text));
    }

    /// <summary>Читает, не мешая писателю: журналы открыты движками на запись.</summary>
    private static string? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            return reader.ReadToEnd();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Начало и конец текста, с пометкой, сколько пропущено между.</summary>
    public static string Excerpt(string text, int head, int tail)
    {
        var lines = text.Split('\n');

        if (lines.Length <= head + tail)
            return text;

        return string.Join('\n', lines.Take(head))
            + $"\n\n… пропущено строк: {lines.Length - head - tail} …\n\n"
            + string.Join('\n', lines.Skip(lines.Length - tail));
    }

    private static readonly Regex Ansi = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

    /// <summary>Прочие ссылки: имя узла оставляем — по нему и разбирают, — путь и запрос нет.</summary>
    private static readonly Regex WebLink = new(
        @"\b(https?://[^/\s""'<>]+)[/?][^\s""'<>]*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Адрес выхода из «Обстановки замера» проверки блокировок: мимо туннеля это
    /// домашний адрес человека. Страна и путь остаются — по ним и разбирают.
    /// </summary>
    /// <remarks>
    /// Найдено в отчёте владельца 06.10: проверка блокировок едет в архив
    /// с 26.09, и всё это время с ней ехал адрес, который NetworkSnapshot
    /// нарочно не пишет.
    /// </remarks>
    private static readonly Regex ExitAddress = new(
        @"(Выход: )(?:\d{1,3}(?:\.\d{1,3}){3}|[0-9a-fA-F]{0,4}(?::[0-9a-fA-F]{0,4}){2,7})",
        RegexOptions.Compiled);

    /// <summary>
    /// Вычищает из текста то, чего в отчёте быть не должно.
    /// </summary>
    /// <remarks>
    /// Порядок важен. Ссылки подписок — первыми и дословно: правило для
    /// прочих ссылок оставило бы от них имя панели, а это лишнее. Путь профиля
    /// Windows заменяется, потому что в нём имя учётной записи человека.
    /// </remarks>
    public static string Redact(string text, IReadOnlyList<string> secrets)
    {
        var result = Ansi.Replace(text, string.Empty);

        foreach (var secret in secrets.OrderByDescending(s => s.Length))
            result = result.Replace(secret, "<ссылка подписки скрыта>", StringComparison.OrdinalIgnoreCase);

        // Ключи, ссылки прокси, секрет TG Proxy, UUID — те же правила, что при записи
        // журнала (LogSecrets): старые журналы и чужие файлы чистятся и здесь.
        result = LogSecrets.Scrub(result);
        result = WebLink.Replace(result, "$1/…");
        result = ExitAddress.Replace(result, "$1<адрес скрыт>");
        result = OwnAddress.Mask(result);

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!string.IsNullOrEmpty(profile))
        {
            result = result.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);

            // Движок под Cygwin пишет тот же путь по-своему:
            // /cygdrive/c/Users/имя — и имя в нём то же.
            var cygwin = "/cygdrive/" + char.ToLowerInvariant(profile[0]) + profile[2..].Replace('\\', '/');
            result = result.Replace(cygwin, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);

            // А родная сборка winws2 (с 07.10) — прямыми слэшами, C:/Users/имя:
            // так пути ей и передаются (WinwsCommandLine.Forward).
            result = result.Replace(profile.Replace('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }
}
