using System.Text.Json.Nodes;
using Zephyron.Core;
using Zephyron.Core.Rules;
using Zephyron.Proxy;
using Zephyron.Subscriptions;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>
/// Настройки пинга и фрагментация рукопожатия с сервером (владелец 07.10).
/// </summary>
public sealed class PingAndFragmentTests
{
    [Fact]
    public void PingUrlFallsBackToCloudflare()
    {
        Assert.Equal(Ping.Cloudflare, Ping.UrlOf(new AppSettings()));
        Assert.Equal(Ping.Google, Ping.UrlOf(new AppSettings { PingUrl = Ping.Google }));
        Assert.Equal("https://example.com/204", Ping.UrlOf(new AppSettings { PingUrl = " https://example.com/204 " }));

        // Негодный адрес в файле — не повод мерить в никуда.
        Assert.Equal(Ping.Cloudflare, Ping.UrlOf(new AppSettings { PingUrl = "cp.cloudflare.com" }));
        Assert.Equal(Ping.Cloudflare, Ping.UrlOf(new AppSettings { PingUrl = "ftp://example.com/" }));
    }

    /// <summary>Потолок замера — 5 с, как было; что бы ни стояло в файле, от 1 до 30.</summary>
    [Fact]
    public void PingTimeoutIsFiveSecondsAndBounded()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), Ping.TimeoutOf(new AppSettings()));
        Assert.Equal(TimeSpan.FromSeconds(2), Ping.TimeoutOf(new AppSettings { PingTimeoutSeconds = 2 }));
        Assert.Equal(TimeSpan.FromSeconds(1), Ping.TimeoutOf(new AppSettings { PingTimeoutSeconds = 0 }));
        Assert.Equal(TimeSpan.FromSeconds(30), Ping.TimeoutOf(new AppSettings { PingTimeoutSeconds = 600 }));
    }

    /// <summary>
    /// «Лучший из двух» убран 07.10, в тот же день, что появился, — а поле
    /// уже записано в файлы настроек. Оно обязано читаться молча: не прочитайся
    /// файл, программа взяла бы настройки по умолчанию целиком — вплоть
    /// до мастера первого запуска.
    /// </summary>
    [Fact]
    public void SettingsWithTheRemovedBestOfTwoStillRead()
    {
        var path = Path.Combine(Path.GetTempPath(), $"zy-settings-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, """{ "OnboardingDone": true, "PingBestOfTwo": true, "PingUrl": "http://www.gstatic.com/generate_204" }""");

            var settings = AppSettings.TryLoad(path, out var result);

            Assert.Equal(AppSettings.ReadResult.Read, result);
            Assert.True(settings.OnboardingDone);
            Assert.Equal(Ping.Google, Ping.UrlOf(settings));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static readonly ProxyServer[] Servers =
    [
        new()
        {
            Protocol = ProxyProtocol.Vless, Tag = "vless-tls", Host = "tls.example.com", Port = 443,
            Credential = "b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33", Security = "tls", Sni = "tls.example.com",
        },
        new()
        {
            Protocol = ProxyProtocol.Vless, Tag = "vless-reality", Host = "reality.example.com", Port = 443,
            Credential = "b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33", Security = "reality", Sni = "www.microsoft.com",
            RealityPublicKey = "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0", RealityShortId = "0123456789abcdef",
            Flow = "xtls-rprx-vision",
        },
        new()
        {
            Protocol = ProxyProtocol.Trojan, Tag = "trojan", Host = "trojan.example.com", Port = 443,
            Credential = "PLACEHOLDER",
        },
        new()
        {
            Protocol = ProxyProtocol.Hysteria2, Tag = "hy2", Host = "hy2.example.com", Port = 443,
            Credential = "PLACEHOLDER",
        },
        new()
        {
            Protocol = ProxyProtocol.Shadowsocks, Tag = "ss", Host = "ss.example.com", Port = 8388,
            Credential = "PLACEHOLDER", Method = "aes-256-gcm",
        },
    ];

    private static JsonObject Outbound(string json, string tag) =>
        JsonNode.Parse(json)!["outbounds"]!.AsArray().OfType<JsonObject>().Single(o => o["tag"]?.GetValue<string>() == tag);

    /// <summary>Xray-вариант flow sing-box отвергает фатально — уходит как обычный vision.</summary>
    [Theory]
    [InlineData("xtls-rprx-vision-udp443", "xtls-rprx-vision")]
    [InlineData("xtls-rprx-direct", null)]
    public void FlowIsWhatSingBoxKnows(string flow, string? expected)
    {
        var engine = RuleSetLoader.Load("mode: selective\nrules: []\n");
        ProxyServer[] servers = [Servers[1] with { Tag = "v", Flow = flow }];
        var result = new SingBoxConfigCompiler().Compile(engine.RuleSet, servers, new SingBoxOptions());
        Assert.Equal(expected, Outbound(result.Json, "v")["flow"]?.GetValue<string>());
    }

    /// <summary>IPv6 выключен в Windows — у адаптера туннеля только адрес IPv4.</summary>
    [Fact]
    public void TunWithoutIpv6HasOnlyV4Address()
    {
        var engine = RuleSetLoader.Load("mode: selective\nrules: []\n");
        var json = new SingBoxConfigCompiler().Compile(engine.RuleSet, Servers, new SingBoxOptions { TunAddressV6 = "" }).Json;
        var tun = JsonNode.Parse(json)!["inbounds"]!.AsArray().OfType<JsonObject>().Single(i => i["type"]?.GetValue<string>() == "tun");
        Assert.Equal(["172.19.0.1/30"], tun["address"]!.AsArray().Select(a => a!.GetValue<string>()));
    }

    /// <summary>Битый ключ REALITY роняет весь sing-box — такой сервер мимо конфига.</summary>
    [Theory]
    [InlineData("jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0", true)]
    [InlineData("", false)]
    [InlineData("short", false)]
    public void RealityNeedsRealKey(string key, bool usable) =>
        Assert.Equal(usable, (Servers[1] with { RealityPublicKey = key }).IsUsableOutbound);

    [Fact]
    public void OutlineKeyWithSlashParses()
    {
        var link = "ss://" + Convert.ToBase64String("chacha20-ietf-poly1305:pass"u8.ToArray()) + "@1.2.3.4:8388/?outline=1#O";
        Assert.True(ProxyUriParser.TryParse(link, out var server, out var error), error);
        Assert.Equal(8388, (int)server!.Port);
    }

    /// <summary>
    /// Режется приветствие TLS поверх TCP — у VLESS (и с REALITY), Trojan;
    /// у Hysteria2 рукопожатие в пакетах QUIC, у Shadowsocks TLS нет. Конфиг
    /// с обоими способами принимает сам движок.
    /// </summary>
    [Theory]
    [InlineData(TlsFragment.Records, "record_fragment")]
    [InlineData(TlsFragment.Packets, "fragment")]
    public void FragmentGoesOnlyToTcpTls(TlsFragment mode, string field)
    {
        var engine = RuleSetLoader.Load("mode: selective\nrules: []\n");
        var result = new SingBoxConfigCompiler().Compile(engine.RuleSet, Servers, new SingBoxOptions { TlsFragment = mode });

        foreach (var tag in new[] { "vless-tls", "vless-reality", "trojan" })
            Assert.True(Outbound(result.Json, tag)["tls"]![field]!.GetValue<bool>(), tag);

        Assert.Null(Outbound(result.Json, "hy2")["tls"]![field]);
        Assert.Null(Outbound(result.Json, "ss")["tls"]);

        var check = SingBoxCheck.Run(result.Json);
        Assert.True(check.Ok, check.Said);
    }

    [Fact]
    public void FragmentIsOffByDefault()
    {
        Assert.Equal(TlsFragment.Off, new AppSettings().TlsFragment);

        var engine = RuleSetLoader.Load("mode: selective\nrules: []\n");
        var result = new SingBoxConfigCompiler().Compile(engine.RuleSet, Servers, new SingBoxOptions());
        var tls = Outbound(result.Json, "vless-tls")["tls"]!;

        Assert.Null(tls["fragment"]);
        Assert.Null(tls["record_fragment"]);
    }

    /// <summary>Пробник меряет тем же путём, что идёт трафик: с той же фрагментацией.</summary>
    [Fact]
    public void ProbeCarriesTheFragment()
    {
        var json = new SingBoxConfigCompiler().CompileProbeConfig(Servers[0], 20808, null, "warn", fragment: TlsFragment.Packets);

        Assert.True(Outbound(json, "probe-out")["tls"]!["fragment"]!.GetValue<bool>());
    }
}
