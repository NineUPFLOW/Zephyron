using Zephyron.Core.Updates;
using Zephyron.Gui.Views;
using Xunit;

namespace Zephyron.Gui.Tests;

/// <summary>Аргументы окна подмены доходят от программы до копии без потерь.</summary>
public sealed class ApplyOptionsTests
{
    [Fact]
    public void ArgumentsRoundTrip()
    {
        var plan = new UpdatePlan { StagedAt = @"C:\Users\Иван Тест\Zephyron\runtime\update\files\Zephyron", Files = 1, Kept = [] };
        var arguments = UpdateInstaller.ApplierArguments(4242, plan, @"C:\Users\Иван Тест\Zephyron\", "0.15.2", "0.15.3", "112 МБ через туннель", null);

        var options = ApplyOptions.Parse([.. arguments]);

        Assert.NotNull(options);
        Assert.Equal(4242, options.ProcessId);
        Assert.Equal(plan.StagedAt, options.Source);
        Assert.Equal(@"C:\Users\Иван Тест\Zephyron", options.Target);
        Assert.Equal("0.15.3", options.To);
        Assert.Equal("112 МБ через туннель", options.Downloaded);
        Assert.Null(options.NotesFile);
        Assert.Null(ApplyOptions.Parse(["--tray"]));
    }
}
