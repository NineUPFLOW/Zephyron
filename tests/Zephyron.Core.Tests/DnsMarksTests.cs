using Zephyron.Proxy;
using Xunit;

namespace Zephyron.Core.Tests;

public sealed class DnsMarksTests
{
    private static DnsSurveyRow Row(Func<DnsSurveyRow, DnsSurveyRow>? with = null)
    {
        var row = new DnsSurveyRow { Provider = DnsSurvey.Providers[0] };
        return with is null ? row : with(row);
    }

    [Fact]
    public void Clean_row_has_no_marks() => Assert.Empty(DnsMarks.Of(Row(r => r with { DohMs = 20 })));

    [Fact]
    public void Blocked_needs_both_encrypted_failures()
    {
        Assert.Empty(DnsMarks.Of(Row(r => r with { DohFailure = "TIMEOUT" })));
        Assert.Equal([DnsMarks.Blocked], DnsMarks.Of(Row(r => r with { DohFailure = "SYN DROP", DotFailure = "TIMEOUT" })));
    }

    [Fact]
    public void Intercepted_or_spoofed_udp_is_marked() =>
        Assert.Equal([DnsMarks.UdpSpoofed], DnsMarks.Of(Row(r => r with { DohMs = 10, Spoofed = 2, SpoofChecked = 5 })));

    [Fact]
    public void Sections_follow_enum_order_and_own_goes_last()
    {
        var own = new DnsProvider("мой", ["1.2.3.4"], Own: true, Group: DnsGroup.Open);
        var sections = DnsMarks.Sections([own, .. DnsSurvey.Providers]);

        Assert.Equal(DnsGroup.Intermediary, sections[0].Group);
        Assert.Equal(DnsGroup.Own, sections[^1].Group);
        Assert.Contains(own, sections[^1].Items);
        Assert.Contains(sections, s => s.Group == DnsGroup.Filtering && s.Items.Any(p => p.Name == "AdGuard Семейный"));
    }
}
