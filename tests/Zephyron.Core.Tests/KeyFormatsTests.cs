using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Xunit;
using Zephyron.Subscriptions;

namespace Zephyron.Core.Tests;

/// <summary>Ключи AmneziaVPN, описание сервера от панели, битый путь транспорта.</summary>
public class KeyFormatsTests
{
    private const string Conf = """
        [Interface]
        PrivateKey = yAnz5TF+lXXJte14tji3zlMNq+hd2rYUIgJBgB3fBmk=
        Address = 10.8.1.2/32
        Jc = 4

        [Peer]
        PublicKey = xTIBA5rboUvnH4htodjb6e697QjLERt1NAB4mZqp8Dg=
        Endpoint = 203.0.113.5:51820
        AllowedIPs = 0.0.0.0/0
        """;

    private static string Key(bool compressed)
    {
        var last = JsonSerializer.Serialize(new { config = Conf });
        var json = JsonSerializer.Serialize(new
        {
            description = "Мой сервер",
            containers = new[] { new Dictionary<string, object> { ["container"] = "amnezia-awg", ["awg"] = new { last_config = last } } },
        });

        var bytes = Encoding.UTF8.GetBytes(json);

        if (compressed)
        {
            using var output = new MemoryStream();
            output.Write([(byte)(bytes.Length >> 24), (byte)(bytes.Length >> 16), (byte)(bytes.Length >> 8), (byte)bytes.Length]);
            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(bytes);
            bytes = output.ToArray();
        }

        return "vpn://" + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AmneziaKeyBecomesWireGuardLink(bool compressed)
    {
        var found = KeyImport.FromText(Key(compressed));

        Assert.Null(found.Problem);
        var link = Assert.Single(found.Keys);
        Assert.True(ProxyUriParser.TryParse(link, out var server, out var error), error);
        Assert.Equal("203.0.113.5", server!.Host);
    }

    [Fact]
    public void BrokenAmneziaKeyIsNamed() =>
        Assert.Equal("ключ AmneziaVPN не читается", KeyImport.FromText("vpn://@@@").Problem);

    [Theory]
    [InlineData("#Germany%3FserverDescription%3DFast%20line")]
    [InlineData("&serverDescription=RmFzdCBsaW5l#Germany")]
    public void PanelDescriptionLeavesTheName(string tail)
    {
        var link = "vless://b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33@de.example.com:443?security=tls&type=tcp" + tail;
        Assert.True(ProxyUriParser.TryParse(link, out var server, out var error), error);
        Assert.Equal("Germany", server!.Tag);
        Assert.Equal("Fast line", server.Description);
    }

    [Theory]
    [InlineData("/ws%zz", false)]
    [InlineData("/ws%20ok", true)]
    public void BrokenEscapeInPathSkipsServer(string path, bool usable) =>
        Assert.Equal(usable, new ProxyServer
        {
            Protocol = ProxyProtocol.Vless, Tag = "p", Host = "p.example.com", Port = 443,
            Credential = "b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33", Transport = "ws", Path = path,
        }.IsUsableOutbound);
}
