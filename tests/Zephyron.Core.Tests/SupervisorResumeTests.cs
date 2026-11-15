using Xunit;
using Zephyron.Supervisor;

namespace Zephyron.Core.Tests;

public sealed class SupervisorResumeTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(0, 5_000, false)]
    [InlineData(0, 35_000, false)]
    [InlineData(0, 35_001, true)]
    [InlineData(1_000, 600_000, true)]
    public void GapOverIntervalPlusThirtySecondsIsSleep(long previous, long now, bool sleep) =>
        Assert.Equal(sleep, ProcessSupervisor.AfterSleep(previous, now, Interval));
}
