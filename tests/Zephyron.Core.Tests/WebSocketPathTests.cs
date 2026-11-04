using System.Text.Json.Nodes;
using Zephyron.Core.Rules;
using Zephyron.Proxy;
using Zephyron.Subscriptions;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>WebSocket и httpupgrade по ссылке Xray: только http/1.1 и ранние данные вместо «?ed=» в пути.</summary>
public sealed class WebSocketPathTests
{
    [Theory]
    [InlineData("/ws?ed=2048", "/ws", 2048)]
    [InlineData("/up?ed=2560&x=1", "/up?x=1", 2560)]
    [InlineData("/plain", "/plain", 0)]
    [InlineData("/q?x=1", "/q?x=1", 0)]
    [InlineData("/q?ed=abc", "/q?ed=abc", 0)]
    public void Early_data_is_split_from_the_path(string path, string expectedPath, int expectedSize)
    {
        Assert.Equal((expectedPath, expectedSize), WebSocketPath.Split(path));
    }

    [Fact]
    public void WebSocket_gets_http11_and_early_data_httpupgrade_loses_the_tail()
    {
        ProxyServer Make(string tag, string transport, string path) => new()
        {
            Protocol = ProxyProtocol.Trojan, Tag = tag, Host = "1.2.3.4", Port = 443, Credential = "PLACEHOLDER",
            Security = "tls", Transport = transport, Path = path, Alpn = ["h2", "http/1.1"],
        };

        var result = new SingBoxConfigCompiler().Compile(
            RuleSetLoader.Load("mode: selective\nrules: []\n").RuleSet,
            [Make("ws", "ws", "/ws?ed=2048"), Make("hu", "httpupgrade", "/up?ed=2560&x=1")],
            new SingBoxOptions());

        var outbounds = JsonNode.Parse(result.Json)!["outbounds"]!.AsArray();
        var ws = outbounds.First(o => (string?)o!["tag"] == "ws")!;
        var hu = outbounds.First(o => (string?)o!["tag"] == "hu")!;

        Assert.Equal("/ws", (string?)ws["transport"]!["path"]);
        Assert.Equal(2048, (int?)ws["transport"]!["max_early_data"]);
        Assert.Equal("Sec-WebSocket-Protocol", (string?)ws["transport"]!["early_data_header_name"]);
        Assert.Equal(["http/1.1"], ws["tls"]!["alpn"]!.AsArray().Select(a => (string?)a));

        Assert.Equal("/up?x=1", (string?)hu["transport"]!["path"]);
        Assert.Null(hu["transport"]!["max_early_data"]);
        Assert.Equal(["http/1.1"], hu["tls"]!["alpn"]!.AsArray().Select(a => (string?)a));
    }
}
