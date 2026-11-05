using Zephyron.Proxy;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>Сети Cloudflare и вердикт «режут длинное приветствие» (ntc.party, 29.10–02.11).</summary>
public sealed class LongHelloTests
{
    private static TargetReport Report(BlockKind kind) => new()
    {
        Host = "example.org",
        Tcp = new ProbeOutcome { Ok = true },
        Tls = new ProbeOutcome { Ok = true, Detail = "короткое принято" },
        Http = new ProbeOutcome { Ok = true },
        Data = new ProbeOutcome { Ok = false },
        Kind = kind,
    };

    [Theory]
    [InlineData("104.16.25.14", true)]
    [InlineData("188.114.96.11", true)]
    [InlineData("172.67.68.209", true)]
    [InlineData("162.159.137.232", true)]
    [InlineData("8.47.69.6", true)]
    [InlineData("8.6.112.6", true)]
    [InlineData("65.9.62.123", false)]
    [InlineData("149.154.167.220", false)]
    [InlineData("::1", false)]
    public void Cloudflare_networks_are_known(string address, bool cloudflare) =>
        Assert.Equal(cloudflare, CloudflareNet.Contains(address));

    /// <summary>Длинное — двумя пакетами (больше MSS 1460), короткое — одним, без X25519MLKEM768.</summary>
    [Fact]
    public void The_compact_hello_fits_one_packet_and_has_no_post_quantum_key()
    {
        var full = BrowserHello.Build("example.com");
        var brief = BrowserHello.Build("example.com", compact: true);

        Assert.True(full.Length > 1460, $"длинное {full.Length} Б");
        Assert.True(brief.Length < 1000, $"короткое {brief.Length} Б");

        // Длины записи и рукопожатия сходятся с тем, что за ними.
        Assert.Equal(brief.Length - 5, (brief[3] << 8) | brief[4]);
        Assert.Equal(brief.Length - 9, (brief[6] << 16) | (brief[7] << 8) | brief[8]);

        // Ключ гибрида: группа 0x11EC и длина 1216 (1184 ML-KEM + 32 X25519).
        Assert.Contains("11EC04C0", Convert.ToHexString(full), StringComparison.Ordinal);
        Assert.DoesNotContain("11EC04C0", Convert.ToHexString(brief), StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_hello_verdict_reads_as_closed_with_its_own_words()
    {
        var report = Report(BlockKind.LongHello);

        Assert.Equal("режут длинное приветствие браузера", report.Describe());
        Assert.Equal("короткое принято", report.Why);
        Assert.Contains("Cloudflare", report.Remedy());
    }
}