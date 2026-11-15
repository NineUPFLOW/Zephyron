using Zephyron.Gui.Views;
using Xunit;

namespace Zephyron.Gui.Tests;

/// <summary>Журнал без стены одинаковых ошибок и служебного шума.</summary>
public sealed class LogTidyTests
{
    [Fact]
    public void RepeatsFoldAndNoiseGoes()
    {
        var (lines, hidden) = LogView.Tidy(
        [
            "ERROR[0727] [2215930570 5.0s] connection: open connection to 149.154.167.220:443 using outbound/direct[direct]: dial tcp 149.154.167.220:443: i/o timeout",
            "ERROR[0728] [1004796521 5.0s] connection: open connection to 149.154.167.221:443 using outbound/direct[direct]: dial tcp 149.154.167.221:443: i/o timeout",
            "ERROR[0729] [99 0.1s] connection: connection download closed: close tcp 1.2.3.4:5->6.7.8.9:443: endpoint not connected",
            "INFO[0730] router: started",
        ]);

        Assert.Equal(2, lines.Count);
        Assert.EndsWith("(×2 подряд)", lines[0]);
        Assert.Equal("INFO[0730] router: started", lines[1]);
        Assert.Equal(2, hidden);
    }
}
