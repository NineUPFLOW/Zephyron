using Zephyron.Core.Rules;
using Zephyron.Supervisor;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>
/// «Не сайт» вместо «ничего не помогло» и журнал прогона — по случаям прогона 25.10.
/// </summary>
public sealed class AutoPickNotASiteTests
{
    private static AutoPickRow Row(string host, params AutoPickTry[] tries)
    {
        var row = new AutoPickRow(new AutoPickTarget(host, "Сервис · Часть"), RoutingMode.Desync, null);

        foreach (var attempt in tries)
            row.Add(attempt);

        return row;
    }

    private static AutoPickTry Direct(int? status, bool? cert, bool works = false) =>
        new(AutoPickWay.Direct, null, null, null, works, TimeSpan.FromSeconds(1), "", status, cert);

    private static AutoPickTry Vpn(string detail, int? status = null) =>
        new(AutoPickWay.Vpn, null, null, "srv", false, TimeSpan.FromSeconds(1), detail, status);

    [Fact]
    public void Server_error_on_every_path_is_not_a_site()
    {
        // discord.media: Cloudflare 520 и напрямую, и через VPN.
        var row = Row("discord.media", Direct(520, true), Vpn("ответ 520", 520));

        Assert.Contains("520", AutoPick.NotASite(row));
    }

    [Fact]
    public void Wrong_certificate_both_ways_is_not_a_site()
    {
        // googlevideo.com: сертификат только на поддомены.
        var row = Row("googlevideo.com", Direct(404, false),
            Vpn("The remote certificate is invalid: RemoteCertificateNameMismatch"));

        Assert.NotNull(AutoPick.NotASite(row));
    }

    [Fact]
    public void Wrong_certificate_only_directly_is_a_block_not_a_verdict()
    {
        // Так же выглядит заглушка провайдера — без подтверждения VPN не судим.
        Assert.Null(AutoPick.NotASite(Row("x.com", Direct(403, false))));
        Assert.Null(AutoPick.NotASite(Row("x.com", Direct(403, false), Vpn("нет ответа за 8 с"))));
    }

    [Fact]
    public void Silence_everywhere_stays_a_failure()
    {
        Assert.Null(AutoPick.NotASite(Row("x.com", Direct(null, null), Vpn("нет ответа за 8 с"))));
    }

    [Fact]
    public void Something_worked_means_it_is_a_site()
    {
        Assert.Null(AutoPick.NotASite(Row("x.com", Direct(520, true), Direct(200, true, works: true))));
    }

    [Fact]
    public void Log_names_every_try_and_the_verdict()
    {
        var site = Row("discord.media", Direct(520, true), Vpn("ответ 520", 520));
        var skipped = Row("es-pio.net");
        skipped.Skip = "имя не разрешается";

        var report = new AutoPickReport("Пресет", [site, skipped],
            new Dictionary<string, TimeSpan> { ["direct"] = TimeSpan.FromSeconds(28) }, ["заметка"]);

        var text = AutoPick.Describe(report, new DateTimeOffset(2026, 10, 25, 23, 15, 0, TimeSpan.Zero));

        Assert.Contains("discord.media — Сервис · Часть", text);
        Assert.Contains("VPN «srv»: ответ 520", text);
        Assert.Contains("итог: не сайт", text);
        Assert.Contains("итог: пропущено: имя не разрешается", text);
        Assert.Contains("direct 0:28", text);
        Assert.Contains("Замечание: заметка", text);
    }
}
