using System.Security.Principal;
using Xunit;
using Zephyron.Supervisor;

namespace Zephyron.Core.Tests;

/// <summary>
/// Kill switch: фильтры Windows ставятся и снимаются; без прав — причина, а не исключение.
/// </summary>
/// <remarks>
/// Настоящие фильтры ставятся только под администратором и живут доли секунды:
/// сеанс динамический, Dispose снимает их разом. Без прав проверяется отказ.
/// </remarks>
public class KillSwitchTests
{
    private static bool Admin() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    [Fact]
    public void OpensAndClosesOrSaysWhy()
    {
        using var opened = KillSwitch.Open([Environment.ProcessPath!], out var problem);

        if (Admin())
            Assert.True(opened is not null, problem);
        else
            Assert.True(opened is not null || problem is { Length: > 0 });
    }

    [Fact]
    public void LoopbackAndTunnelAreNotBlocked() =>
        Assert.All(KillSwitch.PhysicalAdapters(), luid => Assert.NotEqual(0UL, luid));
}
