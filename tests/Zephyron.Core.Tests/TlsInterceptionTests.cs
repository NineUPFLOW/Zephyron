using Zephyron.Core.Diagnostics;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>Кто подписал сертификат «Google»: Google — нет перехвата, антивирус — называем его.</summary>
public sealed class TlsInterceptionTests
{
    [Theory]
    [InlineData("CN=WR2, O=Google Trust Services, C=US")]
    [InlineData("CN=GTS CA 1C3, O=Google Trust Services LLC, C=US")]
    public void GoogleIssuerIsClean(string issuer) => Assert.Null(TlsInterception.Judge(issuer));

    [Theory]
    [InlineData("CN=Kaspersky Anti-Virus Personal Root Certificate, O=AO Kaspersky Lab", "Kaspersky")]
    [InlineData("CN=ESET SSL Filter CA, O=ESET spol. s r. o.", "ESET")]
    [InlineData("CN=Avast Web/Mail Shield Root, O=Avast", "Avast")]
    public void AntivirusIsNamed(string issuer, string product)
    {
        var caught = TlsInterception.Judge(issuer);

        Assert.NotNull(caught);
        Assert.Equal(product, caught!.Product);
    }

    [Fact]
    public void UnknownInterceptorIsStillReported() =>
        Assert.Null(TlsInterception.Judge("CN=Some Corp Proxy CA")!.Product);
}
