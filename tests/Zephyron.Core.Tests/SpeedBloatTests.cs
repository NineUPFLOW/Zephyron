using System.Text.Json;
using Xunit;
using Zephyron.Proxy;

namespace Zephyron.Core.Tests;

public sealed class SpeedBloatTests
{
    [Theory]
    [InlineData(0, 'A')]
    [InlineData(4.9, 'A')]
    [InlineData(5, 'B')]
    [InlineData(29.9, 'B')]
    [InlineData(30, 'C')]
    [InlineData(60, 'D')]
    [InlineData(200, 'E')]
    [InlineData(399, 'E')]
    [InlineData(400, 'F')]
    public void GradeFollowsThresholds(double increase, char grade) =>
        Assert.Equal(grade, SpeedBloat.Grade(increase));

    [Fact]
    public void EntryGradeNeedsBothPings()
    {
        Assert.Null(new SpeedEntry { PingMs = 20 }.Grade());
        Assert.Null(new SpeedEntry { LoadedPingMs = 80 }.Grade());

        var entry = new SpeedEntry { PingMs = 20, LoadedPingMs = 80 };

        Assert.Equal(60, entry.LoadedIncrease());
        Assert.Equal('D', entry.Grade());

        // Под нагрузкой быстрее простоя — шум, а не отрицательный рост.
        Assert.Equal(0, new SpeedEntry { PingMs = 20, LoadedPingMs = 15 }.LoadedIncrease());
    }

    /// <summary>Файл истории до появления поля читается, а новое поле переживает запись.</summary>
    [Fact]
    public void HistoryJsonIsBackwardCompatible()
    {
        var old = """[{"At":"2026-10-01T10:00:00+00:00","Tunnel":false,"DownMbps":50,"PingMs":12}]""";
        var read = JsonSerializer.Deserialize<List<SpeedEntry>>(old)!;

        Assert.Null(read[0].LoadedPingMs);
        Assert.Null(read[0].Grade());

        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");

        try
        {
            SpeedHistory.Add(new SpeedEntry { At = DateTimeOffset.Now, PingMs = 10, LoadedPingMs = 25 }, path);

            Assert.Equal(25, SpeedHistory.Load(path).Single().LoadedPingMs);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
