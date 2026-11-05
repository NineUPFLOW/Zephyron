using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Zephyron.Subscriptions;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>Маскировка AmneziaWG, которой движок не знает, и причины отказа ключа vpn://.</summary>
public sealed class AmneziaMaskingTests
{
    private const string Conf = """
        [Interface]
        PrivateKey = K1mwfCsDrWYcuV34G7RXrAiJoatFKtI6/NPLfwCavVQ=
        Address = 10.8.1.3/32
        Jc = 4

        [Peer]
        PublicKey = 0e6a0OqdsnKPvxM3rX/zImqcLAMx0J9xPni/hixPltk=
        Endpoint = 203.0.113.7:51820
        """;

    private static string Url(byte[] bytes) =>
        "vpn://" + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] QCompress(string text)
    {
        var raw = Encoding.UTF8.GetBytes(text);
        using var packed = new MemoryStream();
        packed.Write([(byte)(raw.Length >> 24), (byte)(raw.Length >> 16), (byte)(raw.Length >> 8), (byte)raw.Length]);

        using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw);

        return packed.ToArray();
    }

    [Fact]
    public void Masking_the_engine_does_not_know_is_kept_and_named()
    {
        var conf = Conf.Replace("Jc = 4", "Jc = 4\nJ1 = <r 2>\nItime = 120", StringComparison.Ordinal);

        var found = KeyImport.FromText(conf, "Новый AWG");

        var link = Assert.Single(found.Keys);
        Assert.Contains("j1=", link);
        Assert.Contains("itime=120", link);
        Assert.Equal("параметров AmneziaWG J1, Itime движок не знает — сервер может не ответить", found.Note);
        Assert.Equal(found.Note, KeyImport.FromText(link).Note);

        // Движку они не передаются: он бы отверг весь конфиг.
        Assert.True(ProxyUriParser.TryParse(link, out var server, out _));
        Assert.DoesNotContain("j1", server!.AmneziaOptions);
    }

    [Fact]
    public void Known_masking_gives_no_note() => Assert.Null(KeyImport.FromText(Conf, "AWG").Note);

    [Fact]
    public void Header_protection_of_AmneziaWG_3_is_kept_and_named()
    {
        var conf = Conf.Replace(
            "Jc = 4",
            "Jc = 4\nS3 = 20\nS4 = 20\nHeaderProtectionKey = 9Jq3kQp1Zq0sS3m0hYkL3Qe8q1b7m2dQyM0rT4uV5w8=\nRandomTrailers = on\nContentPaddingAddition = 0-64",
            StringComparison.Ordinal);

        var found = KeyImport.FromText(conf, "AWG 3");

        var link = Assert.Single(found.Keys);
        Assert.Contains("header_protection_key=", link);
        Assert.Contains("content_padding_addition=0-64", link);
        Assert.True(ProxyUriParser.TryParse(link, out var server, out _));
        Assert.Contains("header_protection_key", server!.AmneziaOptions);
        Assert.Contains("HeaderProtectionKey", found.Note);
        Assert.Equal(found.Note, KeyImport.FromText(link).Note);
    }

    [Fact]
    public void Header_protection_needs_S3_and_S4_above_12()
    {
        string? Get(string k) => k switch { "HeaderProtectionKey" => "9Jq3kQp1Zq0sS3m0hYkL3Qe8q1b7m2dQyM0rT4uV5w8=", "S3" => "12", "S4" => "20", _ => null };

        Assert.Throws<FormatException>(() => AmneziaSettings.From(Get));
        Assert.Throws<FormatException>(() => AmneziaSettings.From(k => k == "RekeyTimeout" ? "x" : null));
        Assert.Contains("\"rekey_timeout\":\"5-10\"", AmneziaSettings.From(k => k == "RekeyTimeout" ? "5 - 10" : null));
    }
    [Fact]
    public void One_sided_AmneziaWG_3_parameters_give_no_note()
    {
        var conf = Conf.Replace(
            "Jc = 4",
            "Jc = 4\nContentPaddingAddition = 0-64\nDisableCookies = on\nRandomTrailers = off",
            StringComparison.Ordinal);

        var found = KeyImport.FromText(conf, "AWG 3");

        Assert.Null(found.Note);
        Assert.Contains("disable_cookies=true", Assert.Single(found.Keys));
    }

    [Fact]
    public void A_vpn_key_with_unknown_masking_is_named()
    {
        var inner = JsonSerializer.Serialize(new { config = Conf.Replace("Jc = 4", "Jc = 4\nJ1 = <r 2>", StringComparison.Ordinal) });
        var json = JsonSerializer.Serialize(new { containers = new[] { new { awg = new { last_config = inner } } } });

        var found = KeyImport.FromText(Url(QCompress(json)));

        Assert.Contains("J1", found.Note);
    }

    [Fact]
    public void A_cut_vpn_key_says_it_is_cut_without_quoting_the_key()
    {
        var whole = Url(QCompress("{\"description\":\"" + string.Concat(Enumerable.Range(0, 60).Select(i => (i * 2654435761L).ToString("x"))) + "\"}"));
        var cut = whole[..(whole.Length - 40)];

        if ((cut.Length - "vpn://".Length) % 4 == 1)
            cut = cut[..^1];

        var found = KeyImport.FromText(cut);

        Assert.Empty(found.Keys);
        Assert.Contains("обрезан", found.Problem);
        Assert.DoesNotContain(cut["vpn://".Length..][..12], found.Problem);
    }

    [Fact]
    public void Unpacked_text_that_is_not_json_is_named_without_its_content()
    {
        var found = KeyImport.FromText(Url(QCompress("[Interface]\nPrivateKey = x\n")));

        Assert.Empty(found.Keys);
        Assert.Contains("не JSON", found.Problem);
        Assert.DoesNotContain("PrivateKey", found.Problem);
    }
}
