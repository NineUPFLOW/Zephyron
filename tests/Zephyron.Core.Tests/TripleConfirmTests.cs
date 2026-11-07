using Zephyron.Supervisor;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>Три удачные пробы подряд: срыв на любой отклоняет кандидата.</summary>
public sealed class TripleConfirmTests
{
    private static async Task<(AutoPickConfirm.Result Result, int Probes, int Pauses)> Run(params bool[] answers)
    {
        int probes = 0, pauses = 0;

        var result = await AutoPickConfirm.RunAsync(
            () => Task.FromResult(answers[probes++]),
            () => { pauses++; return Task.CompletedTask; });

        return (result, probes, pauses);
    }

    [Fact]
    public async Task Three_successes_confirm_with_two_pauses()
    {
        var (result, probes, pauses) = await Run(true, true, true);

        Assert.True(result.Confirmed);
        Assert.Equal("подтверждено 3/3", result.Text);
        Assert.Equal(3, probes);
        Assert.Equal(2, pauses);
    }

    [Fact]
    public async Task Failure_on_second_probe_stops_and_reports_it()
    {
        var (result, probes, _) = await Run(true, false, true);

        Assert.False(result.Confirmed);
        Assert.Equal("сорвалось на 2/3", result.Text);
        Assert.Equal(2, probes);
    }

    [Fact]
    public async Task Failure_on_first_probe_has_no_pause()
    {
        var (result, _, pauses) = await Run(false);

        Assert.Equal("сорвалось на 1/3", result.Text);
        Assert.Equal(0, pauses);
    }
}
