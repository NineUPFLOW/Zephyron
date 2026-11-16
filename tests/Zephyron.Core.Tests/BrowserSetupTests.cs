using Zephyron.Core.Diagnostics;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>Настройки браузеров, которые мешают обходу, и их правка.</summary>
public sealed class BrowserSetupTests
{
    private static readonly Dictionary<string, object?> NoPolicy = [];

    [Fact]
    public void FirefoxTls12AndQuicAndDnsAreFound()
    {
        var prefs = """
            user_pref("security.tls.version.max", 3);
            user_pref("network.trr.mode", 2);
            user_pref("network.proxy.type", 0);
            """;

        var issues = BrowserSetup.FirefoxIssues(prefs).Select(i => i.What).ToList();

        Assert.Contains(issues, w => w.Contains("TLS 1.3"));
        Assert.Contains(issues, w => w.Contains("QUIC"));
        Assert.Contains(issues, w => w.Contains("DNS"));
        Assert.DoesNotContain(issues, w => w.Contains("прокси"));
    }

    [Fact]
    public void FirefoxFixResetsTlsAndTurnsOffDnsAndQuic()
    {
        var fixedPrefs = BrowserSetup.FixFirefox("user_pref(\"security.tls.version.max\", 3);\nuser_pref(\"network.trr.mode\", 2);\n", isUserJs: false);

        Assert.Empty(BrowserSetup.FirefoxIssues(fixedPrefs));
        Assert.Null(BrowserSetup.Pref(fixedPrefs, "security.tls.version.max"));
    }

    [Fact]
    public void UserJsIsOnlyEditedWhereItSetsThePref()
    {
        var userJs = "user_pref(\"browser.startup.page\", 3);\n";

        Assert.Equal(userJs, BrowserSetup.FixFirefox(userJs, isUserJs: true));
    }

    [Fact]
    public void ChromiumDefaultsAreFoundAndFixed()
    {
        var state = """{"browser":{"enabled_labs_experiments":["some-flag@1","enable-quic@1"]},"dns_over_https":{"mode":"secure","templates":"https://x/dns"}}""";

        Assert.Equal(2, BrowserSetup.ChromiumIssues("Chrome", state, NoPolicy).Count());

        var fixedState = BrowserSetup.FixChromium(state);

        Assert.Empty(BrowserSetup.ChromiumIssues("Chrome", fixedState, NoPolicy));
        Assert.Contains("some-flag@1", fixedState);
        Assert.DoesNotContain("enable-quic@1", fixedState);
    }

    [Fact]
    public void PolicyIsReportedButNotFixed()
    {
        var policy = new Dictionary<string, object?> { ["QuicAllowed"] = 1, ["SSLVersionMax"] = "tls1.2" };

        var issues = BrowserSetup.ChromiumIssues("Edge", "{}", policy).ToList();

        Assert.Contains(issues, i => i.What.Contains("QUIC") && !i.Fixable);
        Assert.Contains(issues, i => i.What.Contains("TLS 1.3") && !i.Fixable);
    }
}
