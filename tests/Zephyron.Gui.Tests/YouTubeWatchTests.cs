using Xunit;
using Zephyron.Gui;
using Zephyron.Zapret;

namespace Zephyron.Gui.Tests;

/// <summary>Когда сторож YouTube меняет способ обхода.</summary>
public sealed class YouTubeWatchTests
{
    private static readonly DateTime Now = new(2026, 11, 15, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void OneFailureIsNotEnough() =>
        Assert.False(YouTubeWatch.Due(1, Now, DateTime.MinValue, new Queue<DateTime>()));

    [Fact]
    public void TwoFailuresInARowSwitch() =>
        Assert.True(YouTubeWatch.Due(2, Now, DateTime.MinValue, new Queue<DateTime>()));

    [Fact]
    public void NoSwitchSoonAfterTheLastOne() =>
        Assert.False(YouTubeWatch.Due(2, Now, Now.AddMinutes(-2), new Queue<DateTime>()));

    [Fact]
    public void AFullRoundInHalfAnHourStopsSwitching()
    {
        var recent = new Queue<DateTime>(Enumerable.Range(1, YouTubeRecipes.Recipes.Count).Select(i => Now.AddMinutes(-i * 4)));

        Assert.False(YouTubeWatch.Due(2, Now, Now.AddMinutes(-4), recent));
    }

    [Fact]
    public void OldSwitchesAreForgotten()
    {
        var old = new Queue<DateTime>(Enumerable.Range(1, YouTubeRecipes.Recipes.Count).Select(i => Now.AddMinutes(-40 - i)));

        Assert.True(YouTubeWatch.Due(2, Now, Now.AddMinutes(-41), old));
        Assert.Empty(old);
    }
}
