using Xunit;

namespace Zephyron.Gui.Tests;

public sealed class SiteSpeedTests
{
    [Theory]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("  http://example.com/a b ", "http://example.com/ab")]
    [InlineData("https://example.com/путь", "https://example.com/%D0%BF%D1%83%D1%82%D1%8C")]
    public void InputGetsASchemeAndLosesSpaces(string input, string expected) =>
        Assert.Equal(expected, SiteSpeed.Normalize(input)?.AbsoluteUri);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://example.com")]
    [InlineData("http://")]
    public void NonAddressesAreRejected(string input) => Assert.Null(SiteSpeed.Normalize(input));

    [Theory]
    [InlineData(0, 'A')]
    [InlineData(4.9, 'A')]
    [InlineData(5, 'B')]
    [InlineData(30, 'C')]
    [InlineData(60, 'D')]
    [InlineData(200, 'E')]
    [InlineData(400, 'F')]
    public void GradeFollowsTheThresholds(double increase, char grade) => Assert.Equal(grade, SiteSpeed.Grade(increase));

    [Fact]
    public void StdDevNeedsTwoSamples()
    {
        Assert.Null(SiteSpeed.StdDev([5]));
        Assert.Equal(1, SiteSpeed.StdDev([1, 3])!.Value, 6);
    }
}
