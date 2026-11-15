using Zephyron.Supervisor;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>В отчёт уходит только узел кэша Google, без адреса и подсети человека.</summary>
public sealed class CacheNodeNameTests
{
    [Theory]
    [InlineData("95.25.96.158 => vimpelcom-stw3 (95.25.96.0/22)\n", "vimpelcom-stw3")]
    [InlineData("1.2.3.4 => \n", "пустой ответ")]
    [InlineData("", "пустой ответ")]
    public void OnlyTheNodeIsKept(string answer, string expected) =>
        Assert.Equal(expected, SupportReport.CacheNodeName(answer));
}
