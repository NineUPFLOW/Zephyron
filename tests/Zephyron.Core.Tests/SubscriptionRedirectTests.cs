using System.Net;
using Zephyron.Subscriptions;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>Переадресация панели с https на http того же узла идёт снова по https.</summary>
public sealed class SubscriptionRedirectTests
{
    private static HttpResponseMessage Redirect(string location) =>
        new(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) } };

    [Fact]
    public void DowngradeOnSameHostGoesBackToHttps() =>
        Assert.Equal(new Uri("https://svyaz-26.ru/sub"),
            SubscriptionClient.SecureRedirect(Redirect("http://svyaz-26.ru/sub"), new Uri("https://svyaz-26.ru/sub/")));

    [Fact]
    public void OtherHostsAndPlainAnswersAreLeftAlone()
    {
        Assert.Null(SubscriptionClient.SecureRedirect(Redirect("http://evil.example/sub"), new Uri("https://svyaz-26.ru/sub/")));
        Assert.Null(SubscriptionClient.SecureRedirect(new HttpResponseMessage(HttpStatusCode.OK), new Uri("https://svyaz-26.ru/sub/")));
    }
}
