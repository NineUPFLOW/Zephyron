using Zephyron.Core.Rules;
using Zephyron.Supervisor;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>
/// Перепроверка перед заменой, «не сайт» без HTTPS и предложение убрать VPN — по прогону 25.10.
/// </summary>
public sealed class AutoPickConfirmTests
{
    private static AutoPickTry Try(AutoPickWay way, bool works, double seconds = 1, string? recipe = null,
        int? status = null, string detail = "") =>
        new(way, recipe, recipe, way == AutoPickWay.Vpn ? "srv" : null, works, TimeSpan.FromSeconds(seconds),
            detail, status ?? (works ? 200 : null));

    private static AutoPickRow Row(RoutingMode mode, string? recipe, params AutoPickTry[] tries)
    {
        var row = new AutoPickRow(new AutoPickTarget("a.example.com", "Пример"), mode, recipe);

        foreach (var attempt in tries)
            row.Add(attempt);

        row.Choice = AutoPick.Choose(row);
        return row;
    }

    [Fact]
    public void Single_failure_of_the_current_route_asks_for_both_repeats()
    {
        // rutracker.org: нынешний рецепт не дождался, пресет открыл — один раз.
        var row = Row(RoutingMode.Desync, "stealth",
            Try(AutoPickWay.Recipe, false, recipe: "stealth"), Try(AutoPickWay.Preset, true));

        Assert.Equal(AutoPickWay.Preset, row.Choice!.Way);
        Assert.Equal(
            [(AutoPickWay.Recipe, "stealth"), (AutoPickWay.Preset, (string?)null)],
            AutoPick.NeedsRepeat(row));
    }

    [Fact]
    public void Current_route_that_works_on_repeat_is_kept()
    {
        var row = Row(RoutingMode.Desync, "stealth",
            Try(AutoPickWay.Recipe, false, recipe: "stealth"), Try(AutoPickWay.Preset, true),
            Try(AutoPickWay.Recipe, true, recipe: "stealth"), Try(AutoPickWay.Preset, true));

        Assert.Null(row.Choice);
    }

    [Fact]
    public void Winner_that_fails_on_repeat_gives_way_to_a_steady_one()
    {
        var row = Row(RoutingMode.Desync, "gone",
            Try(AutoPickWay.Recipe, false, recipe: "gone"), Try(AutoPickWay.Recipe, false, recipe: "gone"),
            Try(AutoPickWay.Preset, true), Try(AutoPickWay.Preset, false),
            Try(AutoPickWay.Recipe, true, 0.3, "fast"), Try(AutoPickWay.Recipe, false, 0.3, "fast"),
            Try(AutoPickWay.Recipe, true, 0.5, "steady"), Try(AutoPickWay.Recipe, true, 0.6, "steady"));

        Assert.Equal("steady", row.Choice!.Recipe);
        Assert.Empty(AutoPick.NeedsRepeat(row));
    }

    [Fact]
    public void Recipes_are_ranked_by_their_slowest_sample()
    {
        var row = Row(RoutingMode.Desync, "gone",
            Try(AutoPickWay.Recipe, false, recipe: "gone"), Try(AutoPickWay.Recipe, false, recipe: "gone"),
            Try(AutoPickWay.Recipe, true, 0.1, "jumpy"), Try(AutoPickWay.Recipe, true, 2.0, "jumpy"),
            Try(AutoPickWay.Recipe, true, 0.5, "even"), Try(AutoPickWay.Recipe, true, 0.6, "even"));

        Assert.Equal("even", row.Choice!.Recipe);
    }

    [Fact]
    public void Working_vpn_is_left_when_a_page_opens_without_it()
    {
        // notion.com: VPN работает, напрямую страница 200 — экономим трафик VPN (26.10).
        var row = Row(RoutingMode.Proxy, null, Try(AutoPickWay.Direct, true), Try(AutoPickWay.Vpn, true, status: 302));

        Assert.False(row.Choice!.Optional);
        Assert.Equal(RoutingMode.Direct, row.Choice.Mode);
        Assert.Equal([(AutoPickWay.Direct, (string?)null)], AutoPick.NeedsRepeat(row));
    }

    [Fact]
    public void Factory_vpn_is_only_offered()
    {
        // ChatGPT, Gemini: заводской VPN — отказ по стране бывает внутри приложения.
        var row = new AutoPickRow(new AutoPickTarget("gemini.google.com", "Нейросети · Gemini"), RoutingMode.Proxy, null)
        {
            FactoryVpn = true,
        };

        row.Add(Try(AutoPickWay.Direct, true));
        row.Add(Try(AutoPickWay.Vpn, true));

        Assert.True(AutoPick.Choose(row)!.Optional);
    }

    [Fact]
    public void Recipe_missing_after_a_preset_switch_is_a_broken_route()
    {
        // Рецепт «Выбрано вручную: valheim» не находится в другом пресете — обход пропал.
        var row = new AutoPickRow(new AutoPickTarget("obsidian.md", "Obsidian"), RoutingMode.Desync, "Выбрано вручную: valheim")
        {
            RecipeMissing = true,
        };

        row.Add(Try(AutoPickWay.Recipe, true, 0.3, "multisplit"));
        row.Add(Try(AutoPickWay.Recipe, true, 0.3, "multisplit"));

        Assert.False(AutoPick.CurrentWorks(row));
        Assert.Equal("multisplit", AutoPick.Choose(row)!.Recipe);
    }

    [Fact]
    public void Broken_current_without_a_steady_way_is_not_called_working()
    {
        // netflix.com 26.10: пресет молчит, VPN ответил раз из двух — «работает» тут неправда.
        var row = Row(RoutingMode.Desync, null,
            Try(AutoPickWay.Preset, false), Try(AutoPickWay.Vpn, true, status: 301),
            Try(AutoPickWay.Preset, false), Try(AutoPickWay.Vpn, false));

        Assert.Null(row.Choice);
        Assert.False(AutoPick.CurrentWorks(row));
    }

    [Fact]
    public void Geo_refusal_is_not_a_reason_to_leave_vpn()
    {
        // 403 напрямую — «ответ», но без VPN сайт как раз и не работает.
        var row = Row(RoutingMode.Proxy, null,
            Try(AutoPickWay.Direct, true, status: 403), Try(AutoPickWay.Vpn, true));

        Assert.Null(row.Choice);
    }

    [Fact]
    public void Server_without_https_is_not_a_site()
    {
        // rtcp.on.epicgames.com: рукопожатие есть, ответа нет — везде, и через VPN.
        const string closed = "не помогает — сервер закрыл соединение";

        var row = Row(RoutingMode.Desync, null,
            Try(AutoPickWay.Direct, false, detail: closed),
            Try(AutoPickWay.Preset, false, detail: closed),
            Try(AutoPickWay.Vpn, false, detail: "Received an unexpected EOF or 0 bytes from the transport stream."));

        Assert.Contains("не отвечает по HTTPS", AutoPick.NotASite(row));
    }

    [Fact]
    public void Silence_directly_and_eof_via_vpn_is_still_a_failure()
    {
        // rutracker.org 25.10: напрямую тишина, VPN-сервер не довёз — это не «не сайт».
        var row = Row(RoutingMode.Desync, null,
            Try(AutoPickWay.Direct, false, detail: "не помогает — не дождались"),
            Try(AutoPickWay.Vpn, false, detail: "Received an unexpected EOF or 0 bytes from the transport stream."));

        Assert.Null(AutoPick.NotASite(row));
    }
}
