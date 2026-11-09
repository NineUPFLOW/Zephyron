using System.Net;
using Xunit;
using Zephyron.Core.Updates;
using Zephyron.Proxy;
using Zephyron.Supervisor;

namespace Zephyron.Core.Tests;

/// <summary>Мелочи по обращениям пользователей: страна выхода, суммы, журналы, DNS.</summary>
public sealed class FieldReportsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("zy-fr-").FullName;
    private readonly string _exitPath = ExitCountry.CachePath;
    private readonly string _trustPath = DnsTrust.CachePath;

    public FieldReportsTests()
    {
        ExitCountry.CachePath = Path.Combine(_dir, "exit.json");
        DnsTrust.CachePath = Path.Combine(_dir, "trust.json");
    }

    public void Dispose()
    {
        ExitCountry.CachePath = _exitPath;
        DnsTrust.CachePath = _trustPath;
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Trace_loc_is_parsed_and_junk_is_refused()
    {
        Assert.Equal("DE", ExitCountry.Parse("fl=1\nip=1.2.3.4\nloc=DE\ncolo=FRA\n"));
        Assert.Null(ExitCountry.Parse("loc=xx\nloc=DEU"));
        Assert.Null(ExitCountry.Parse(null));
    }

    [Fact]
    public void Measured_country_beats_the_flag_and_cache_is_fresh_for_six_hours()
    {
        const string Tag = "🇷🇺 Основной";
        var t0 = DateTimeOffset.UtcNow;

        Assert.True(ExitCountry.IsDomestic(Tag));          // замера нет — по флагу
        Assert.True(ExitCountry.Due(Tag, t0));

        ExitCountry.Store(Tag, "DE", t0);

        Assert.Equal("DE", ExitCountry.Of(Tag));
        Assert.False(ExitCountry.IsDomestic(Tag));          // замер важнее флага
        Assert.False(ExitCountry.Due(Tag, t0.AddHours(5)));
        Assert.True(ExitCountry.Due(Tag, t0.AddHours(7)));

        ExitCountry.Store("🇩🇪 Берлин", "RU", t0);
        Assert.True(ExitCountry.IsDomestic("🇩🇪 Берлин"));  // флаг врёт — выход дома
    }

    [Fact]
    public void Cloudflare_503_in_window_are_counted_from_real_log_lines()
    {
        var now = DateTimeOffset.Parse("2026-10-26T17:50:00Z");
        string[] lines =
        [
            "2026-10-26T17:41:08.030293Z  WARN tg_ws_proxy_rs::ws_client: CF WS DC3 failed on kws3.pclead.co.uk: HTTP 503 from server",
            "2026-10-26T17:41:08.123198Z  WARN tg_ws_proxy_rs::ws_client: CF WS DC2 failed on kws2.pclead.co.uk: HTTP 503 from server",
            "2026-10-26T17:41:08.123198Z  INFO tg_ws_proxy_rs::server: Listening on 127.0.0.1:1443",
            "2026-10-26T16:00:00.000000Z  WARN tg_ws_proxy_rs::ws_client: CF WS DC2 failed on kws2.x.co.uk: HTTP 503 from server",
        ];

        Assert.Equal(2, TgWsProxyHealth.Count(lines, TimeSpan.FromMinutes(10), now));
        Assert.Equal(3, TgWsProxyHealth.Count(lines, TimeSpan.FromHours(2), now));

        var log = Path.Combine(_dir, "tg.log");
        File.WriteAllLines(log, lines);
        Assert.Equal(2, TgWsProxyHealth.RecentCf503(TimeSpan.FromMinutes(10), log, now));
        Assert.Equal(0, TgWsProxyHealth.RecentCf503(TimeSpan.FromMinutes(10), Path.Combine(_dir, "none.log"), now));
    }

    [Fact]
    public void Archive_hash_is_read_from_the_release_footer()
    {
        var sha = new string('a', 63) + "B";
        var html = $"<pre><code>Zephyron-1.4.3.zip   {sha}\nZephyron.exe   {new string('c', 64)}\n</code></pre>";

        Assert.Equal(sha.ToLowerInvariant(), UpdateCheck.HashFromPage(html, "1.4.3"));
        Assert.Null(UpdateCheck.HashFromPage(html, "1.4.4"));
        Assert.Null(UpdateCheck.HashFromPage("<p>нет подвала</p>", "1.4.3"));
        Assert.Null(UpdateCheck.HashFromPage("Zephyron-1.4.3.zip   " + new string('a', 40), "1.4.3"));
    }

    [Fact]
    public void Run_marker_names_the_start_and_pid()
    {
        Assert.Equal("==== запуск 2026-11-09 21:18:14 (PID 42) ====",
            SupervisedService.RunMarker(new DateTime(2026, 11, 9, 21, 18, 14), 42));
    }

    [Fact]
    public void Virtual_adapter_first_in_order_is_warned_about()
    {
        IReadOnlyList<IPAddress> dns = [IPAddress.Parse("26.0.0.1")];
        IReadOnlyList<IPAddress> lan = [IPAddress.Parse("192.168.1.1")];

        var warn = Doctor.VirtualDnsFirst([("Radmin VPN", "Famatech Radmin VPN Ethernet Adapter", dns), ("Wi-Fi", "Intel", lan)]);

        Assert.NotNull(warn);
        Assert.Contains("«Radmin VPN»", warn!.Text);
        Assert.Null(Doctor.VirtualDnsFirst([("Wi-Fi", "Intel", lan), ("Radmin VPN", "Radmin", dns)]));
    }

    [Fact]
    public void Dns_trust_remembers_interception_and_names_a_clean_alternative()
    {
        var bad = new DnsProvider("Bad", ["1.1.1.1"], "1.1.1.1", "one.one");
        var good = new DnsProvider("Good", ["9.9.9.9"], "9.9.9.9", "dns.good");

        Assert.Null(DnsTrust.IsIntercepted("1.1.1.1"));

        var rows = new[]
        {
            new DnsSurveyRow { Provider = bad, DohMs = 20, UdpAnswered = 1, Intercepted = true },
            new DnsSurveyRow { Provider = good, DohMs = 30, UdpAnswered = 1 },
        };

        DnsTrust.Record(rows);

        Assert.True(DnsTrust.IsIntercepted("1.1.1.1"));
        Assert.False(DnsTrust.IsIntercepted("9.9.9.9"));
        Assert.Equal("Good", DnsTrust.CleanAlternative(rows));
    }
}
