using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>Свой адрес человека в журналах и отчётах — звёздочками (владелец, 25.10).</summary>
public sealed class OwnAddressTests : IDisposable
{
    public void Dispose() => OwnAddress.Forget();

    [Fact]
    public void ALearnedAddressIsMaskedEverywhereInTheText()
    {
        OwnAddress.Learn(" 95.25.96.76\n");

        Assert.Equal("Выход: **** — мимо туннеля; снова ****",
            OwnAddress.Mask("Выход: 95.25.96.76 — мимо туннеля; снова 95.25.96.76"));
    }

    [Fact]
    public void ServersAndNonAddressesAreLeftAlone()
    {
        OwnAddress.Learn("не адрес");
        Assert.Null(OwnAddress.Known);

        OwnAddress.Learn("2a02:2698:24::1");
        Assert.Equal("сервер 149.154.167.220, мой ****", OwnAddress.Mask("сервер 149.154.167.220, мой 2a02:2698:24::1"));
    }
}
