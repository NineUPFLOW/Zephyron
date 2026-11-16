using Zephyron.Subscriptions;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>
/// Заглушка панели для нелюбимого клиента (подписка Trust, 28.09).
/// </summary>
public sealed class SubscriptionStubTests
{
    private static ProxyServer Server(string host, string security, ushort port = 443) => new()
    {
        Protocol = ProxyProtocol.Vless,
        Tag = host,
        Host = host,
        Port = port,
        Credential = "PLACEHOLDER",
        Transport = "tcp",
        Security = security,
    };

    private static SubscriptionInfo Info(params ProxyServer[] servers) => new() { Servers = servers };

    [Fact]
    public void Many_servers_on_one_address_without_encryption_is_a_stub()
    {
        Assert.True(SubscriptionClient.LooksLikeStub(Info(
            Server("convert-flow.net", "none"), Server("convert-flow.net", "none"), Server("convert-flow.net", ""))));
    }

    [Fact]
    public void Real_subscriptions_are_not()
    {
        // Разные адреса.
        Assert.False(SubscriptionClient.LooksLikeStub(Info(Server("a.example", "none"), Server("b.example", "none"))));

        // Один адрес, но с Reality — так бывает у настоящих: один вход, много выходов.
        Assert.False(SubscriptionClient.LooksLikeStub(Info(Server("gw.example", "reality"), Server("gw.example", "reality"))));

        // Подписка из одного сервера.
        Assert.False(SubscriptionClient.LooksLikeStub(Info(Server("one.example", "none"))));
    }

    [Fact]
    public void ZeroAddressesAndPortOneArePlaceholders()
    {
        // 16.11: ссылками «Основной» на 0.0.0.0:443 и «Во время ограничений» на 0.0.0.0:1.
        var info = Info(Server("0.0.0.0", "tls"), Server("0.0.0.0", "none", port: 1), Server("84.38.181.190", "tls"), Server("::", "tls"));

        Assert.Equal(3, SubscriptionClient.Placeholders(info));
        Assert.False(SubscriptionClient.IsPlaceholder(Server("breaker.sbs", "tls")));
    }
}

/// <summary>Запертая ссылка Happ — понятный ответ вместо «ни ключей, ни ссылки».</summary>
public sealed class HappCryptTests
{
    [Fact]
    public void EncryptedHappLinkTellsWhereToGetAPlainOne()
    {
        var found = KeyImport.FromText("happ://crypt4/EkAoNMIK2fEKCL5CB8mISVXnyAko353DBLun6jv6xCGKn1I6");

        Assert.Empty(found.Keys);
        Assert.Null(found.SubscriptionUrl);
        Assert.StartsWith("это зашифрованная ссылка Happ", found.Problem);
    }
}
