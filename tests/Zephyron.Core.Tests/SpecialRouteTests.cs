using Zephyron.Core.Rules;
using Zephyron.Supervisor;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>Особые маршруты: пометка в файле маршрутов, её судьба при смене режима, пины набора и отчёт.</summary>
public sealed class SpecialRouteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "special-" + Guid.NewGuid().ToString("N"));

    public SpecialRouteTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Rules => Path.Combine(_dir, "rules.user.yaml");

    private string Hosts => Path.Combine(_dir, "hosts");

    private static readonly Dictionary<string, string> InstagramPins = new(StringComparer.OrdinalIgnoreCase)
    {
        ["instagram.com"] = "31.25.239.132",
        ["www.instagram.com"] = "31.25.239.132",
    };

    [Fact]
    public void MarkSurvivesSaveAndLoad()
    {
        var file = UserRulesFile.Load(Rules);
        file.Set(MatchKind.HostList, "config/lists/instagram.txt", RoutingMode.Direct);
        file.SetSpecial(MatchKind.HostList, "config/lists/instagram.txt", RoutingMode.Direct, SpecialRoute.RelayPin, "geohide-eu", InstagramPins);
        file.Save();

        var entry = Assert.Single(UserRulesFile.Load(Rules).Entries);

        Assert.Equal(SpecialRoute.RelayPin, entry.Special);
        Assert.Equal("geohide-eu", entry.Pin);
        Assert.Equal("31.25.239.132", entry.PinHosts!["www.instagram.com"]);
        Assert.Contains("пин посредника", RulesShare.Describe(entry));
    }

    [Fact]
    public void SameModeKeepsMarkOtherModeDropsIt()
    {
        var file = UserRulesFile.Load(Rules);
        file.SetSpecial(MatchKind.HostList, "config/lists/youtube.txt", RoutingMode.Desync, SpecialRoute.ServicePin, null, InstagramPins);

        file.Set(MatchKind.HostList, "config/lists/youtube.txt", RoutingMode.Desync, recipe: "pass");
        Assert.Equal(SpecialRoute.ServicePin, file.Entries[0].Special);

        file.Set(MatchKind.HostList, "config/lists/youtube.txt", RoutingMode.Proxy);
        Assert.Null(file.Entries[0].Special);
    }

    [Fact]
    public void UnknownWordIsAnOrdinaryRoute()
    {
        File.WriteAllText(Rules, "rules:\n  - match: hostlist\n    value: \"config/lists/a.txt\"\n    mode: direct\n    special: future-thing\n");

        Assert.Null(Assert.Single(UserRulesFile.Load(Rules).Entries).Special);
    }

    [Fact]
    public void BundlePinsAreReadWithTheirKind()
    {
        var entries = new[]
        {
            new UserRuleEntry { Match = MatchKind.HostList, Value = "config/lists/instagram.txt", Mode = RoutingMode.Direct, Special = SpecialRoute.RelayPin, PinHosts = InstagramPins },
            new UserRuleEntry { Match = MatchKind.HostList, Value = "config/lists/x.txt", Mode = RoutingMode.Desync, Special = SpecialRoute.ServicePin,
                PinHosts = new Dictionary<string, string>
                {
                    ["x.com"] = "104.244.42.1", ["bad.example"] = "не адрес",
                    ["a.com\n1.2.3.4 bank.ru"] = "1.2.3.4", ["b.com #x"] = "1.2.3.4",
                } },
        };

        var pins = RulesShare.Pins(entries);

        Assert.Equal(3, pins.Count);
        Assert.True(pins.Single(p => p.Name == "x.com").Desync);
        Assert.DoesNotContain(pins, p => p.Name == "bad.example");
    }

    [Fact]
    public void ReportComparesWithHosts()
    {
        var file = UserRulesFile.Load(Rules);
        file.SetSpecial(MatchKind.HostList, "config/lists/instagram.txt", RoutingMode.Direct, SpecialRoute.RelayPin, null, InstagramPins);
        file.Save();
        File.WriteAllLines(Hosts, ["31.25.239.132 instagram.com"]);

        var report = SupportReport.SpecialRoutes(Rules, Hosts);

        Assert.Contains("instagram.com → 31.25.239.132: стоит", report);
        Assert.Contains("www.instagram.com → 31.25.239.132: НЕТ в hosts", report);
    }
}
