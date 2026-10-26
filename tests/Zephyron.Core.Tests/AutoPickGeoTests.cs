using Zephyron.Core.Rules;
using Zephyron.Supervisor;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>
/// Заглушка страны, уровни ответа и маршрут сервиса вместо дубля — по замечаниям 26.10.
/// </summary>
public sealed class AutoPickGeoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "autopick-geo-" + Guid.NewGuid().ToString("N"));

    public AutoPickGeoTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Rules => Path.Combine(_dir, "rules.user.yaml");

    private string Journal => Path.Combine(_dir, "journal.json");

    private static AutoPickTry Try(AutoPickWay way, int? status, bool works = true, string? recipe = null, bool stub = false) =>
        new(way, recipe, recipe, way == AutoPickWay.Vpn ? "srv" : null, works, TimeSpan.FromSeconds(0.5), "",
            status, true, stub);

    private static AutoPickRow Row(AutoPickTarget target, RoutingMode mode, string? recipe, params AutoPickTry[] tries)
    {
        var row = new AutoPickRow(target, mode, recipe);

        foreach (var attempt in tries)
            row.Add(attempt);

        row.Choice = AutoPick.Choose(row);
        return row;
    }

    private static readonly AutoPickTarget Netflix =
        new("netflix.com", "Кино и сериалы · Netflix", "config/lists/netflix.txt");

    [Theory]
    [InlineData("HTTP/1.1 403 Forbidden\r\nContent-Length: 13\r\n\r\nNot Available", 60, true)]
    [InlineData("HTTP/1.1 200 OK\r\n\r\n<h1>Сервис недоступен в вашей стране</h1>", 80, true)]
    [InlineData("HTTP/1.1 200 OK\r\n\r\n<html>обычная страница</html>", 60, false)]
    [InlineData("HTTP/1.1 200 OK\r\n\r\n<p>not available</p>", 20000, false)]
    public void Stub_is_a_short_body_with_a_refusal(string head, int total, bool stub) =>
        Assert.Equal(stub, RecipeTrial.IsStub(head, total));

    [Fact]
    public void Page_beats_a_refusal()
    {
        Assert.Equal(2, Try(AutoPickWay.Direct, 200).Level);
        Assert.Equal(2, Try(AutoPickWay.Direct, 403).Level);
        Assert.Equal(1, Try(AutoPickWay.Direct, 403, stub: true).Level);
        Assert.Equal(1, Try(AutoPickWay.Direct, 200, stub: true).Level);
        Assert.Equal(2, Try(AutoPickWay.Vpn, 302).Level);
        Assert.Equal(0, Try(AutoPickWay.Direct, null, works: false).Level);
    }

    [Fact]
    public void Geo_refusal_everywhere_but_vpn_goes_to_vpn()
    {
        // netflix.com 26.10: «403 Not Available» любым рецептом, через VPN — страница.
        var row = Row(Netflix, RoutingMode.Desync, "Telegram",
            Try(AutoPickWay.Recipe, 403, recipe: "Telegram", stub: true),
            Try(AutoPickWay.Recipe, 403, recipe: "multisplit", stub: true),
            Try(AutoPickWay.Vpn, 200));

        Assert.Equal(RoutingMode.Proxy, row.Choice!.Mode);
        Assert.Contains("по стране", row.Choice.Why);
    }

    [Fact]
    public void Same_refusal_via_vpn_is_no_better()
    {
        // medium.com 26.10: 403 и через пресет, и через VPN — VPN тут ничего не даёт.
        var row = Row(Netflix, RoutingMode.Desync, null,
            Try(AutoPickWay.Preset, 403, stub: true), Try(AutoPickWay.Vpn, 403, stub: true));

        Assert.Null(row.Choice);
    }

    [Fact]
    public void Refusal_everywhere_is_not_a_reason_to_change()
    {
        // CDN отвечает 404 на корне и напрямую, и через VPN: это и есть его работа.
        var row = Row(Netflix, RoutingMode.Desync, null,
            Try(AutoPickWay.Preset, 404), Try(AutoPickWay.Vpn, 404));

        Assert.Null(row.Choice);
    }

    [Fact]
    public void Service_name_is_written_to_the_service_route_and_its_duplicate_is_removed()
    {
        // Прошлый прогон записал дубль *.netflix.com своим правилом.
        var old = new AutoPickRow(new AutoPickTarget("netflix.com", "Кино и сериалы · Netflix"), RoutingMode.Desync, null);
        old.Add(Try(AutoPickWay.Recipe, 200, recipe: "Telegram"));
        old.Choice = new(AutoPickWay.Recipe, RoutingMode.Desync, "Telegram", "Telegram", "старое");
        AutoPick.Apply(new AutoPickReport("p", [old], new Dictionary<string, TimeSpan>(), []), Rules, Journal,
            _ => (RoutingMode.Desync, "Telegram", null));

        // Теперь то же имя с маршрутом сервиса.
        var row = new AutoPickRow(Netflix, RoutingMode.Desync, "Telegram") { Duplicate = true };
        row.Add(Try(AutoPickWay.Vpn, 200));
        row.Choice = new(AutoPickWay.Vpn, RoutingMode.Proxy, null, null, "новое");

        var outcome = AutoPick.Apply(new AutoPickReport("p", [row], new Dictionary<string, TimeSpan>(), []),
            Rules, Journal, _ => (RoutingMode.Proxy, null, null));

        Assert.True(outcome.Single().Written);

        var file = UserRulesFile.Load(Rules);
        Assert.DoesNotContain(file.Entries, e => e.Match == MatchKind.Domain);
        Assert.Contains(file.Entries, e => e.Match == MatchKind.HostList
            && e.Value == "config/lists/netflix.txt" && e.Mode == RoutingMode.Proxy);

        // Старый прогон помечен снятым, новый откатывается по правилу на список.
        var runs = AutoPickJournal.Load(Journal);
        Assert.True(runs[0].Entries.Single().Restored);
        Assert.Null(AutoPickJournal.Restore(runs[1], runs[1].Entries.Single(), Rules, Journal));
        Assert.Empty(UserRulesFile.Load(Rules).Entries);
    }

    [Fact]
    public void Redirect_counts_the_same_directly_and_via_vpn()
    {
        // youtu.be: напрямую уводит на youtube.com, через VPN — тот же 302.
        var row = Row(new AutoPickTarget("youtu.be", "YouTube · Ссылки"), RoutingMode.Direct, null,
            Try(AutoPickWay.Direct, 302), Try(AutoPickWay.Vpn, 302));

        Assert.Null(row.Choice);
    }

    [Theory]
    // npmjs.com, fandom.com 26.10: проверка на робота — не отказ по стране.
    [InlineData("HTTP/1.1 403 Forbidden\r\nCf-Mitigated: challenge\r\n\r\nJust a moment...", 403, null, false)]
    // netflix.com: голый 403.
    [InlineData("HTTP/1.1 403 Forbidden\r\n\r\nNot Available", 403, null, true)]
    // claude.ai: перенаправление на страницу отказа.
    [InlineData("HTTP/1.1 302 Found\r\n\r\n", 302, "https://claude.com/app-unavailable-in-region", true)]
    [InlineData("HTTP/1.1 302 Found\r\n\r\n", 302, "https://www.youtube.com/watch", false)]
    [InlineData("HTTP/1.1 404 Not Found\r\n\r\n<html>нет такой страницы</html>", 404, null, false)]
    public void Country_refusal_follows_block_check(string head, int status, string? location, bool refused) =>
        Assert.Equal(refused, RecipeTrial.Refused(head, 100, status, location));

    [Fact]
    public void Json_refusal_is_not_a_country_stub() =>
        Assert.False(RecipeTrial.IsStub(
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n{\"error\":\"access denied\"}", 80));

    private void WriteDuplicate(string recipe)
    {
        var old = new AutoPickRow(new AutoPickTarget("netflix.com", "Кино и сериалы · Netflix"), RoutingMode.Desync, null);
        old.Choice = new(AutoPickWay.Recipe, RoutingMode.Desync, recipe, recipe, "старое");
        AutoPick.Apply(new AutoPickReport("p", [old], new Dictionary<string, TimeSpan>(), []), Rules, Journal,
            _ => (RoutingMode.Desync, recipe, null));
    }

    private IReadOnlyList<AutoPickOutcome> WriteService(RoutingMode got)
    {
        var row = new AutoPickRow(Netflix, RoutingMode.Desync, null);
        row.Choice = new(AutoPickWay.Vpn, RoutingMode.Proxy, null, null, "новое");

        return AutoPick.Apply(new AutoPickReport("p", [row], new Dictionary<string, TimeSpan>(), []),
            Rules, Journal, _ => (got, null, "#1 чужое"));
    }

    [Fact]
    public void Shadowed_service_write_puts_the_duplicate_back()
    {
        WriteDuplicate("Telegram");

        Assert.False(WriteService(RoutingMode.Desync).Single().Written);

        var rule = Assert.Single(UserRulesFile.Load(Rules).Entries);
        Assert.Equal(("*.netflix.com", "Telegram"), (rule.Value, rule.Recipe));
        Assert.All(AutoPickJournal.Load(Journal).SelectMany(r => r.Entries), e => Assert.False(e.Restored));
    }

    [Fact]
    public void Duplicate_written_twice_is_unwound_to_the_end()
    {
        WriteDuplicate("Telegram");
        WriteDuplicate("multisplit");

        Assert.True(WriteService(RoutingMode.Proxy).Single().Written);

        Assert.DoesNotContain(UserRulesFile.Load(Rules).Entries, e => e.Match == MatchKind.Domain);
        Assert.All(AutoPickJournal.Load(Journal).Take(2).SelectMany(r => r.Entries), e => Assert.True(e.Restored));
    }

    [Fact]
    public void Second_name_of_the_same_list_is_not_written_over_the_first()
    {
        AutoPickRow Of(string host)
        {
            var row = new AutoPickRow(Netflix with { Host = host }, RoutingMode.Desync, null);
            row.Choice = new(AutoPickWay.Vpn, RoutingMode.Proxy, null, null, "новое");
            return row;
        }

        var outcome = AutoPick.Apply(
            new AutoPickReport("p", [Of("netflix.com"), Of("nflxext.com")], new Dictionary<string, TimeSpan>(), []),
            Rules, Journal, _ => (RoutingMode.Proxy, null, null));

        Assert.Equal([true, false], outcome.Select(o => o.Written));
        Assert.Single(UserRulesFile.Load(Rules).Entries);
    }

    [Fact]
    public void Part_turned_off_by_the_user_is_not_switched_back_on()
    {
        // «Выключить часть» в «Маршрутах» — решение человека (аудит 26.10).
        var rules = UserRulesFile.Load(Rules);
        rules.SetOff(MatchKind.HostList, "config/lists/netflix.txt");
        rules.Save();

        var outcome = WriteService(RoutingMode.Proxy).Single();

        Assert.False(outcome.Written);
        Assert.True(UserRulesFile.Load(Rules).Entries.Single().Off);
    }

    [Fact]
    public void Working_duplicate_moves_into_the_service_route_unchanged()
    {
        var row = Row(Netflix with { }, RoutingMode.Desync, "Telegram", Try(AutoPickWay.Recipe, 200, recipe: "Telegram"));
        Assert.Null(row.Choice);

        var dup = new AutoPickRow(Netflix, RoutingMode.Desync, "Telegram") { Duplicate = true };
        dup.Add(Try(AutoPickWay.Recipe, 200, recipe: "Telegram"));

        var move = AutoPick.Choose(dup)!;
        Assert.Equal(("Telegram", RoutingMode.Desync, false), (move.Recipe, move.Mode, move.Optional));
    }
}
