using System.Diagnostics;
using System.Text.Json;
using Zephyron.Core.Rules;
using Zephyron.Proxy;
using Zephyron.Subscriptions;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>
/// Бесплатный WARP: один выход, MASQUE.
/// </summary>
/// <remarks>
/// Второй выход, по WireGuard, отсюда убран вместе со всей обвязкой —
/// заведением ключей, регистрацией в Cloudflare и хранением записи. Работать
/// он не мог: Cloudflare кладёт идентификатор записи в поле <c>reserved</c>
/// каждого пакета, а sing-box 1.14 убрал это поле из настройки пира вовсе.
/// Проверено на живой сети: рукопожатие проходит, данные не возвращаются
/// ни на одном из пяти портов.
/// </remarks>
public partial class WarpTests
{
    /// <summary>
    /// MASQUE описывается двумя строками: узла у него нет, движок выбирает
    /// его сам. Поля server и server_port он объявляет незнакомыми
    /// и отказывается читать конфиг целиком — то есть лишнее поле здесь
    /// роняет туннель, а не портит один выход.
    /// </summary>
    [Fact]
    public void MasqueOutboundCarriesNoServerAddress()
    {
        var outbound = Compile(Warp.MasqueServer())
            .GetProperty("outbounds").EnumerateArray()
            .First(o => o.GetProperty("tag").GetString() == Warp.MasqueTag);

        Assert.Equal("masque", outbound.GetProperty("type").GetString());
        Assert.False(outbound.TryGetProperty("server", out _));
        Assert.False(outbound.TryGetProperty("server_port", out _));
    }

    /// <summary>MASQUE идёт поверх HTTP/2, а не QUIC.</summary>
    /// <remarks>
    /// Замер 23.09 на 1.14.1-extended-2.7.2: по QUIC узел 162.159.198.2
    /// молчит, по HTTP/2 — warp=on за 173–204 мс, с пустым кэшем и с полным.
    /// Сними этот ключ — и WARP снова мёртв.
    /// </remarks>
    [Fact]
    public void MasqueGoesOverHttp2()
    {
        var outbound = Compile(Warp.MasqueServer())
            .GetProperty("outbounds").EnumerateArray()
            .First(o => o.GetProperty("tag").GetString() == Warp.MasqueTag);

        Assert.True(outbound.GetProperty("use_http2").GetBoolean());
    }

    /// <summary>
    /// Даже когда адрес выяснен заранее, в MASQUE его подставлять нельзя:
    /// поля для него нет, и конфиг пробника перестал бы читаться.
    /// </summary>
    [Fact]
    public void ResolvedAddressIsNotPinnedIntoMasque()
    {
        var json = new SingBoxConfigCompiler()
            .CompileProbeConfig(Warp.MasqueServer(), 21080, null, "warn", "104.16.24.84");

        var outbound = JsonDocument.Parse(json).RootElement
            .GetProperty("outbounds").EnumerateArray()
            .First(o => o.GetProperty("tag").GetString() == "probe-out");

        Assert.False(outbound.TryGetProperty("server", out _));
    }

    /// <summary>
    /// Кэш движка включается только вместе с MASQUE: в нём лежит учётная
    /// запись, которую движок заводит себе сам, и без кэша он регистрировался
    /// бы заново при каждом запуске.
    /// </summary>
    [Fact]
    public void EngineCacheAppearsOnlyWithMasque()
    {
        var withMasque = Compile(Warp.MasqueServer()).GetProperty("experimental");

        Assert.True(withMasque.TryGetProperty("cache_file", out var cache));
        Assert.True(cache.GetProperty("enabled").GetBoolean());
        Assert.True(cache.GetProperty("store_masque_config").GetBoolean());

        // Полным путём, и это не придирка: относительный решается от текущего
        // каталога движка, где runtime может не существовать вовсе. Движок
        // его не создаёт, а падает на старте — вместе со всем туннелем.
        var path = cache.GetProperty("path").GetString()!;

        Assert.True(Path.IsPathFullyQualified(path), $"путь к кэшу не полный: {path}");
        Assert.True(Directory.Exists(Path.GetDirectoryName(path)), "каталог кэша не заведён");

        var withoutMasque = Compile(Ordinary()).GetProperty("experimental");

        Assert.False(withoutMasque.TryGetProperty("cache_file", out _));
    }

    /// <summary>
    /// Память подставных адресов включает кэш и без MASQUE — с полем
    /// <c>store_fakeip</c>, но без <c>store_masque_config</c>: учётной записи
    /// Cloudflare у подписки нет, и хранить там нечего.
    /// </summary>
    /// <remarks>
    /// Замер 20.10: после перезапуска движка github.com получал другой
    /// подставной адрес (198.18.0.7 → 198.18.0.5), а соединение на старый —
    /// отказ. Поле <c>store_fakeip</c> движок 1.14.3 принимает (проверка
    /// <c>sing-box check</c> в этом же тесте); <c>store_selected</c> он не
    /// знает — выбор в селекторе хранится всегда.
    /// </remarks>
    [Fact]
    public void FakeIpMemoryTurnsOnTheEngineCache()
    {
        var options = new SingBoxOptions { StoreFakeIp = true };

        var root = Compile(options, Ordinary());
        var cache = root.GetProperty("experimental").GetProperty("cache_file");

        Assert.True(cache.GetProperty("enabled").GetBoolean());
        Assert.True(cache.GetProperty("store_fakeip").GetBoolean());
        Assert.False(cache.TryGetProperty("store_masque_config", out _));
        Assert.True(Path.IsPathFullyQualified(cache.GetProperty("path").GetString()!));

        var (ok, said) = SingBoxCheck.Run(root.GetRawText());
        Assert.True(ok, said);

        // С MASQUE — оба поля: учётная запись и таблица адресов живут в одном файле.
        var both = Compile(options, Warp.MasqueServer()).GetProperty("experimental").GetProperty("cache_file");

        Assert.True(both.GetProperty("store_fakeip").GetBoolean());
        Assert.True(both.GetProperty("store_masque_config").GetBoolean());
    }

    /// <summary>
    /// WARP — запасной выход: в селекторе он есть всегда, а в автоподборе —
    /// только когда живых серверов подписки нет.
    /// </summary>
    /// <remarks>
    /// Прежде он стоял в автоподборе наравне с ними, в расчёте, что они
    /// быстрее. Замер 23.09: одиночный запрос через WARP — 147 мс, через
    /// серверы подписки — 220–265, и автоподбор брал его. А под нагрузкой
    /// он держит одно-два соединения: Telegram висел на бесконечном
    /// подключении.
    /// </remarks>
    [Fact]
    public void WarpIsOnlyAReserveInAutoLatency()
    {
        var root = Compile(Ordinary(), Warp.MasqueServer());

        Assert.Contains(Warp.MasqueTag, Members(root, "auto"));
        Assert.Contains("NL", Members(root, "auto"));

        Assert.Equal(["NL"], Members(root, "auto-latency"));
    }

    /// <summary>
    /// Все серверы подписки отмечены мёртвыми — WARP в автоподбор всё равно не входит:
    /// на него переводит надзор (владелец, 08.11), а urltest остаётся над серверами VPN.
    /// </summary>
    [Fact]
    public void AllSubscriptionServersDeadKeepsWarpOutOfAutoLatency()
    {
        var root = Compile(new SingBoxOptions { DeadServerTags = new HashSet<string> { "NL" } },
            Ordinary(), Warp.MasqueServer());

        Assert.Equal(["NL"], Members(root, "auto-latency"));
        Assert.Contains(Warp.MasqueTag, Members(root, "auto"));
    }

    [Fact]
    public void FallbackWarpFollowsPathAndSubscription()
    {
        var withKeys = new AppSettings { SubscriptionUrl = "https://x.example/sub" };

        Assert.True(Warp.UsesFallback(withKeys));
        Assert.False(Warp.UsesFallback(withKeys with { WarpFallback = false }));
        Assert.False(Warp.UsesFallback(withKeys with { WarpEnabled = true }));
        Assert.False(Warp.UsesFallback(new AppSettings()));

        Assert.Equal(["NL", Warp.MasqueTag], Warp.TunnelExits(withKeys, [Ordinary()]).Select(s => s.Tag));
        Assert.Equal(["NL"], Warp.TunnelExits(withKeys with { WarpFallback = false }, [Ordinary()]).Select(s => s.Tag));
        Assert.Equal([Warp.MasqueTag], Warp.TunnelExits(withKeys with { WarpEnabled = true }, [Ordinary()]).Select(s => s.Tag));
    }

    [Fact]
    public void ExitsOnlyEscalateToWarpOnceThenBypass()
    {
        Assert.True(TunnelBypass.GoesToWarp(BypassAction.Engage, hasWarp: true, onWarp: false));
        Assert.False(TunnelBypass.GoesToWarp(BypassAction.Engage, hasWarp: true, onWarp: true));
        Assert.False(TunnelBypass.GoesToWarp(BypassAction.Engage, hasWarp: false, onWarp: false));
        Assert.False(TunnelBypass.GoesToWarp(BypassAction.Release, hasWarp: true, onWarp: false));
    }

    /// <summary>
    /// «Только зарубежные»: WARP — домашний выход (замер 01.10: loc=RU, colo=DME),
    /// и запасным его не берут. Выбрать руками по-прежнему можно — селектор знает его.
    /// </summary>
    [Fact]
    public void ForeignExitsOnlyLeavesWarpOutOfAutoLatency()
    {
        var root = Compile(new SingBoxOptions { ForeignExitsOnly = true, DeadServerTags = new HashSet<string> { "NL" } },
            Ordinary(), Warp.MasqueServer());

        Assert.Equal(["NL"], Members(root, "auto-latency"));
        Assert.Contains(Warp.MasqueTag, Members(root, "auto"));

        // Не до пустоты: кроме WARP, выходов нет — он и остаётся.
        Assert.Equal([Warp.MasqueTag],
            Members(Compile(new SingBoxOptions { ForeignExitsOnly = true }, Warp.MasqueServer()), "auto-latency"));
    }

    /// <summary>Без подписки WARP и есть автоподбор.</summary>
    [Fact]
    public void WarpAloneFillsAutoLatency()
    {
        var root = Compile(Warp.MasqueServer());

        Assert.Equal([Warp.MasqueTag], Members(root, "auto-latency"));
    }

    private static List<string?> Members(JsonElement root, string group) =>
        root.GetProperty("outbounds").EnumerateArray()
            .First(o => o.GetProperty("tag").GetString() == group)
            .GetProperty("outbounds").EnumerateArray()
            .Select(m => m.GetString())
            .ToList();

    /// <summary>
    /// MASQUE не замеряется отдельным пробником, и это его свойство, а не
    /// сбой: запись движок держит в кэше работающего экземпляра, а файл занят
    /// им же. Меряет его сам движок через Clash API.
    /// </summary>
    [Fact]
    public void MasqueIsNotMeasurableByTheProbe()
    {
        Assert.False(Warp.MasqueServer().IsMeasurable);
        Assert.True(Warp.MasqueServer().IsSelfRegistering);
        Assert.True(Ordinary().IsMeasurable);
    }

    /// <summary>Выход ровно один, и ключей он не требует.</summary>
    [Fact]
    public void OnlyMasqueRemains()
    {
        Assert.Equal([Warp.MasqueTag], Warp.Exits().Select(s => s.Tag));
        Assert.Empty(Warp.MasqueServer().Credential);
    }

    /// <summary>
    /// Сквозная проверка настоящим движком. Пропускается, если его нет:
    /// в репозитории он не хранится.
    /// </summary>
    [Fact]
    public void ConfigWithWarpPassesSingBoxCheck()
    {
        var singBox = FindSingBox();
        if (singBox is null)
            return;

        var engine = RuleSetLoader.Load("""
            mode: selective
            rules:
              - match: domain
                value: "*.rutracker.org"
                mode: proxy
                server: "auto"
            """);

        var result = new SingBoxConfigCompiler().Compile(
            engine.RuleSet,
            [Ordinary(), Warp.MasqueServer()],
            new SingBoxOptions());

        var path = Path.Combine(Path.GetTempPath(), $"zephyron-warp-check-{Guid.NewGuid():N}.json");

        try
        {
            SingBoxConfigCompiler.WriteToFile(path, result.Json);

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = singBox,
                ArgumentList = { "check", "-c", path },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;

            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, $"sing-box check не пройден: {stderr}");
        }
        finally
        {
            File.Delete(path);
            File.Delete(SingBoxConfigCompiler.StampPathFor(path));
        }
    }

    private static ProxyServer Ordinary() => new()
    {
        Protocol = ProxyProtocol.Hysteria2,
        Tag = "NL",
        Host = "nl.example.com",
        Port = 4443,
        Credential = "PLACEHOLDER",
        Security = "tls",
    };

    private static ProxyServer Vless(string tag, string host = "203.0.113.5") => new()
    {
        Protocol = ProxyProtocol.Vless,
        Tag = tag,
        Host = host,
        Port = 443,
        Credential = "11111111-1111-1111-1111-111111111111",
    };

    /// <summary>
    /// Посредник MASQUE — скрытый выход: сам есть в конфиге, на него указывает
    /// profile.detour, а в селекторе, автоподборе и пулах его нет.
    /// </summary>
    [Fact]
    public void DetourIsHiddenOutboundReferencedByMasqueProfile()
    {
        var config = Compile(
            new SingBoxOptions { WarpDetour = Vless("🇪🇪 Эстония", "198.51.100.7") },
            Warp.MasqueServer());

        var outbounds = config.GetProperty("outbounds").EnumerateArray().ToList();
        var masque = outbounds.First(o => o.GetProperty("tag").GetString() == Warp.MasqueTag);
        var via = masque.GetProperty("profile").GetProperty("detour").GetString();

        Assert.Equal(SingBoxOptions.WarpDetourTag, via);

        var hidden = outbounds.Single(o => o.GetProperty("tag").GetString() == via);
        Assert.Equal("vless", hidden.GetProperty("type").GetString());
        Assert.Equal("198.51.100.7", hidden.GetProperty("server").GetString());

        foreach (var group in outbounds.Where(o => o.TryGetProperty("outbounds", out _)))
            Assert.DoesNotContain(group.GetProperty("outbounds").EnumerateArray(), m => m.GetString() == via);

        Assert.DoesNotContain("Эстония", masque.ToString());

        // Регистрация через сервер, сам туннель — напрямую: detour у выхода не пишется.
        Assert.False(masque.TryGetProperty("detour", out _));
    }

    /// <summary>Страна выбрана — через сервер идёт и сам туннель (detour у выхода).</summary>
    [Fact]
    public void CountryRoutesWholeTunnelThroughDetour()
    {
        var masque = Compile(
                new SingBoxOptions { WarpDetour = Vless("🇪🇪 Эстония"), WarpExitThroughDetour = true },
                Warp.MasqueServer())
            .GetProperty("outbounds").EnumerateArray()
            .First(o => o.GetProperty("tag").GetString() == Warp.MasqueTag);

        Assert.Equal(SingBoxOptions.WarpDetourTag, masque.GetProperty("detour").GetString());
        Assert.Equal(SingBoxOptions.WarpDetourTag, masque.GetProperty("profile").GetProperty("detour").GetString());

        var estonia = Vless("🇪🇪 Эстония");
        Assert.True(Warp.ExitsThrough(new AppSettings { WarpCountryTag = "🇪🇪 эстония" }, estonia));
        Assert.False(Warp.ExitsThrough(new AppSettings(), estonia));
        Assert.False(Warp.ExitsThrough(new AppSettings { WarpCountryTag = "DE" }, estonia));
        Assert.False(Warp.ExitsThrough(new AppSettings { WarpCountryTag = "DE" }, null));
    }

    /// <summary>Без посредника конфиг прежний: ни profile, ни лишнего выхода.</summary>
    [Fact]
    public void NoDetourLeavesConfigAsBefore()
    {
        var outbounds = Compile(Warp.MasqueServer()).GetProperty("outbounds").EnumerateArray().ToList();

        Assert.False(outbounds.First(o => o.GetProperty("tag").GetString() == Warp.MasqueTag).TryGetProperty("profile", out _));
        Assert.DoesNotContain(outbounds, o => o.GetProperty("tag").GetString() == SingBoxOptions.WarpDetourTag);
    }

    /// <summary>Посредник без MASQUE в конфиге никуда не попадает.</summary>
    [Fact]
    public void DetourWithoutMasqueIsIgnored()
    {
        var outbounds = Compile(new SingBoxOptions { WarpDetour = Vless("DE") }, Vless("NL", "198.51.100.9"))
            .GetProperty("outbounds").EnumerateArray().ToList();

        Assert.DoesNotContain(outbounds, o => o.GetProperty("tag").GetString() == SingBoxOptions.WarpDetourTag);
    }

    /// <summary>Адрес посредника виден десинку так же, как адреса серверов туннеля.</summary>
    [Fact]
    public void DetourAddressIsListedAmongTunnelEndpoints()
    {
        var json = new SingBoxConfigCompiler().Compile(
            RuleSetLoader.Load("mode: selective\nrules: []\n").RuleSet,
            [Warp.MasqueServer()],
            new SingBoxOptions { WarpDetour = Vless("DE", "198.51.100.7") }).Json;

        var path = Path.Combine(Path.GetTempPath(), $"warp-detour-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);

        try
        {
            Assert.Contains(System.Net.IPAddress.Parse("198.51.100.7"), TunnelEndpoints.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static readonly IReadOnlySet<string> NoDead = new HashSet<string>();
    private static readonly IReadOnlyDictionary<string, double> NoSpeed = new Dictionary<string, double>();

    [Fact]
    public void DetourPrefersCountryThenPinnedThenFastestAlive()
    {
        ProxyServer[] subs = [Vless("A"), Vless("B"), Vless("C")];
        var speed = new Dictionary<string, double> { ["A"] = 90, ["B"] = 40, ["C"] = 10 };
        var dead = new HashSet<string> { "C" };

        Assert.Equal("C", Warp.Detour(new AppSettings { WarpCountryTag = "C", PreferredServer = "B" }, subs, dead, speed)!.Tag);
        Assert.Equal("A", Warp.Detour(new AppSettings { PreferredServer = "a" }, subs, dead, speed)!.Tag);
        Assert.Equal("B", Warp.Detour(new AppSettings(), subs, dead, speed)!.Tag);
        Assert.Equal("A", Warp.Detour(new AppSettings(), subs, NoDead, new Dictionary<string, double> { ["A"] = 1 })!.Tag);
    }

    [Fact]
    public void DetourFallsBackToFirstFitWhenAllDeadAndSkipsUnfit()
    {
        var wireguard = Vless("WG") with { Protocol = ProxyProtocol.Wireguard };
        ProxyServer[] subs = [Warp.MasqueServer(), wireguard, Vless("A"), Vless("B")];

        Assert.Equal("A", Warp.Detour(new AppSettings(), subs, new HashSet<string> { "A", "B" }, NoSpeed)!.Tag);
        Assert.Null(Warp.Detour(new AppSettings(), [], NoDead, NoSpeed));
        Assert.Null(Warp.Detour(new AppSettings(), [Warp.MasqueServer()], NoDead, NoSpeed));

        // Сервер, выбранный страной, исчез из подписки, — берём общий порядок.
        Assert.Equal("A", Warp.Detour(new AppSettings { WarpCountryTag = "gone" }, subs, NoDead, NoSpeed)!.Tag);
    }

    [Fact]
    public void TraceVerdictComparesCountryAndNeedsWarp()
    {
        var ee = WarpTrace.Parse("fl=1\nip=203.0.113.7\nloc=ee\ncolo=TLL\nwarp=on\n");
        var plain = WarpTrace.Parse("loc=EE\nwarp=off\n");

        Assert.Equal(new WarpExit("EE", true), ee);
        Assert.Equal(WarpExitVerdict.Match, WarpTrace.Judge(ee, "EE"));
        Assert.Equal(WarpExitVerdict.Match, WarpTrace.Judge(ee, null));
        Assert.Equal(WarpExitVerdict.Mismatch, WarpTrace.Judge(ee, "DE"));
        Assert.Equal(WarpExitVerdict.Down, WarpTrace.Judge(plain, "EE"));
        Assert.Equal(WarpExitVerdict.Down, WarpTrace.Judge(null, null));
    }

    /// <summary>Настройка страны: сохраняется, а у старых настроек без поля — «как есть».</summary>
    [Fact]
    public void CountrySettingRoundTripsAndOldFilesReadAsNone()
    {
        var path = Path.Combine(Path.GetTempPath(), $"warp-country-{Guid.NewGuid():N}.json");

        try
        {
            new AppSettings { WarpEnabled = true, WarpCountryTag = "🇪🇪 Эстония" }.Save(path);
            Assert.Equal("🇪🇪 Эстония", AppSettings.Load(path).WarpCountryTag);

            File.WriteAllText(path, "{\"WarpEnabled\": true}");
            Assert.Null(AppSettings.Load(path).WarpCountryTag);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static JsonElement Compile(params ProxyServer[] servers) =>
        Compile(new SingBoxOptions(), servers);

    private static JsonElement Compile(SingBoxOptions options, params ProxyServer[] servers)
    {
        var engine = RuleSetLoader.Load("""
            mode: selective
            rules:
              - match: domain
                value: "*.rutracker.org"
                mode: proxy
                server: "auto"
            """);

        var result = new SingBoxConfigCompiler().Compile(engine.RuleSet, servers, options);

        return JsonDocument.Parse(result.Json).RootElement.Clone();
    }

    private static string? FindSingBox()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var tools = Path.Combine(directory.FullName, "tools");

            if (Directory.Exists(tools))
            {
                var found = Directory
                    .EnumerateFiles(tools, "sing-box.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (found is not null)
                    return found;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
