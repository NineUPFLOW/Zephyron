using Zephyron.Core.Rules;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>
/// Разбор вписанного в поле «Маршрутов»: поиск это или имя сайта.
/// </summary>
public sealed class DomainInputTests
{
    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("  Example.COM ", "example.com")]
    [InlineData("https://example.com/page?x=1", "example.com")]
    [InlineData("http://cdn.example.com:8443/a", "cdn.example.com")]
    [InlineData("*.example.com", "example.com")]
    [InlineData("example.com/", "example.com")]
    [InlineData("example.com.", "example.com")]
    [InlineData("пример.рф", "xn--e1afmkfd.xn--p1ai")]
    public void ANameIsTakenFromWhatPeopleType(string text, string expected) =>
        Assert.Equal(expected, DomainInput.Normalize(text));

    /// <summary>Без точки — это поиск по сервису, а не сайт.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("дискорд")]
    [InlineData("youtube")]
    [InlineData("my site.com")]
    [InlineData("example.")]
    [InlineData("144.76")]
    [InlineData("*.144.76.194.29")]
    [InlineData("https://144.76.194.29/page")]
    [InlineData(null)]
    public void ASearchIsNotAName(string? text) =>
        Assert.Null(DomainInput.Normalize(text));
}
