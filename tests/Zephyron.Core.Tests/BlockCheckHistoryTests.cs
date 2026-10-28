using Zephyron.Proxy;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>«Было — стало» читается из того же текста, что пишет окно.</summary>
public class BlockCheckHistoryTests
{
    private static string Line(string name, string verdict) =>
        name.PadRight(38) + "ок".PadRight(8) + "1.3".PadRight(8) + "—".PadRight(9) + "ок".PadRight(9) + verdict;

    private static readonly string Report = string.Join("\r\n",
        "Проверка блокировок — 28.10.2026 13:47",
        "Движки:  остановлены — видно, что закрыто на самом деле",
        "",
        "ИМЯ                                   TCP     TLS     ПОРТ 80  ДАННЫЕ   ВЕРДИКТ",
        new string('-', 78),
        Line("discord.com", "DPI по TLS"),
        Line("instagram.com [чз]", "доступен"),
        Line("youtube.com", "резолвер даёт нерабочий адрес"),
        "",
        "ПОЧЕМУ",
        Line("not-a-row.com", "что угодно"));

    [Fact]
    public void ReadsVerdictsAndEnginesUntilTheTableEnds()
    {
        var (engines, verdicts) = BlockCheckHistory.Parse(Report);

        Assert.False(engines);
        Assert.Equal(3, verdicts.Count);
        Assert.Equal("DPI по TLS", verdicts["discord.com"]);
        Assert.Equal("доступен", verdicts["instagram.com"]);
    }

    [Fact]
    public void OnlyNamesCheckedBothTimesAndChangedAreReported()
    {
        var (_, was) = BlockCheckHistory.Parse(Report);

        var changes = BlockCheckHistory.Compare(was,
        [
            ("discord.com", "доступен"),
            ("instagram.com", "доступен"),
            ("new-site.org", "DPI по TLS"),
        ]);

        var change = Assert.Single(changes);
        Assert.Equal(new VerdictChange("discord.com", "DPI по TLS", "доступен"), change);
    }

    [Fact]
    public void LongNamesMatchTheirTrimmedColumn()
    {
        var host = new string('a', 45) + ".com";
        var (_, was) = BlockCheckHistory.Parse(string.Join("\n", "ИМЯ ", Line(BlockCheckHistory.Key(host), "доступен"), ""));

        Assert.Single(BlockCheckHistory.Compare(was, [(host, "DPI по TLS")]));
    }
}
