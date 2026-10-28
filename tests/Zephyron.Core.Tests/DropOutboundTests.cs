using Xunit;
using System.Text.Json.Nodes;
using Zephyron.Supervisor;

namespace Zephyron.Core.Tests;

/// <summary>Сервер, на котором sing-box отказался подняться, вырезается из конфига.</summary>
public class DropOutboundTests
{
    private const string Config = """
        {
          "outbounds": [
            { "type": "selector", "tag": "proxy", "outbounds": ["auto", "a", "b"], "default": "b" },
            { "type": "urltest", "tag": "auto", "outbounds": ["a", "b"] },
            { "type": "vless", "tag": "a" },
            { "type": "vless", "tag": "b", "flow": "bad" },
            { "type": "direct", "tag": "direct" }
          ],
          "route": { "final": "proxy" }
        }
        """;

    [Fact]
    public void BrokenServerLeavesConfigAndGroups()
    {
        var (json, tag) = SingBoxService.DropOutbound(Config, 3)!.Value;
        var outbounds = JsonNode.Parse(json)!["outbounds"]!.AsArray();

        Assert.Equal("b", tag);
        Assert.Equal(4, outbounds.Count);
        Assert.DoesNotContain("\"b\"", json);
        Assert.Equal("auto", outbounds[0]!["default"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(0)]  // группа, а не сервер
    [InlineData(9)]  // нет такого номера
    [InlineData(4)]  // на него ссылается маршрут
    public void RefusesWhatIsNotAServerOrIsUsedElsewhere(int index)
    {
        var config = Config.Replace("\"final\": \"proxy\"", "\"final\": \"direct\"");
        Assert.Null(SingBoxService.DropOutbound(config, index));
    }

    [Fact]
    public void LastMemberOfGroupStays() =>
        Assert.Null(SingBoxService.DropOutbound(
            """{ "outbounds": [ { "type": "urltest", "tag": "auto", "outbounds": ["a"] }, { "type": "vless", "tag": "a" } ] }""", 1));
}
