using System.Drawing;
using Xunit;

namespace Zephyron.Gui.Tests;

/// <summary>Сохранённое место окна зажимается в подключённые мониторы.</summary>
public class WindowPlacementTests
{
    private static readonly Rectangle[] One = [new(0, 0, 1920, 1040)];

    [Fact]
    public void OffScreenWindowComesBackToTheRemainingMonitor() =>
        Assert.Equal(new Rectangle(760, 100, 1160, 760), WindowPlacement.Clamp(new(3000, 100, 1160, 760), One));

    [Fact]
    public void OversizedWindowShrinksToTheWorkArea() =>
        Assert.Equal(new Rectangle(0, 0, 1920, 1040), WindowPlacement.Clamp(new(-50, -50, 4000, 3000), One));

    [Fact]
    public void WindowStaysOnItsOwnMonitor()
    {
        Rectangle[] two = [new(0, 0, 1920, 1040), new(1920, 0, 1920, 1040)];
        Assert.Equal(new Rectangle(2000, 50, 1160, 760), WindowPlacement.Clamp(new(2000, 50, 1160, 760), two));
    }
}
