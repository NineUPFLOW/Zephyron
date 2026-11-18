using System.Net;
using Xunit;
using Zephyron.Gui;

namespace Zephyron.Gui.Tests;

/// <summary>Когда и на что сторож Cloudflare меняет рецепт.</summary>
public sealed class CloudflareWatchTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RecipesGoRoundAndForeignOnesStartFromTheFirst()
    {
        Assert.Equal(CloudflareWatch.Rotation[0], CloudflareWatch.Next("Google (сервисы)"));
        Assert.Equal(CloudflareWatch.Rotation[0], CloudflareWatch.Next(null));
        Assert.Equal(CloudflareWatch.Rotation[1], CloudflareWatch.Next(CloudflareWatch.Rotation[0]));
        Assert.Equal(CloudflareWatch.Rotation[0], CloudflareWatch.Next(CloudflareWatch.Rotation[^1]));
    }

    [Fact]
    public void OneFailureWaitsAndAFullRoundStops()
    {
        Assert.False(CloudflareWatch.Due(1, Now, DateTime.MinValue, new Queue<DateTime>()));
        Assert.True(CloudflareWatch.Due(2, Now, DateTime.MinValue, new Queue<DateTime>()));
        Assert.False(CloudflareWatch.Due(2, Now, Now.AddMinutes(-1), new Queue<DateTime>()));

        var recent = new Queue<DateTime>(Enumerable.Range(1, CloudflareWatch.Rotation.Count).Select(i => Now.AddMinutes(-i * 4)));
        Assert.False(CloudflareWatch.Due(2, Now, Now.AddMinutes(-4), recent));
    }

    [Fact]
    public void WildcardRulesWatchTheSiteAndWww()
    {
        Assert.Equal(["nexusmods.com", "www.nexusmods.com"], CloudflareWatch.Names("*.nexusmods.com"));
        Assert.Equal(["www.x.com"], CloudflareWatch.Names("*.www.x.com"));
        Assert.Equal(["x.com"], CloudflareWatch.Names("x.com"));
    }

    [Fact]
    public void PinsToAnOpenAddressAndRotatesOnlyWhenNoneOpens()
    {
        var open = IPAddress.Parse("104.18.42.54");
        var shut = IPAddress.Parse("172.64.145.202");

        Assert.Equal((true, false, (IPAddress?)open), CloudflareWatch.Decide(2, [open], null, false));
        Assert.Equal((true, false, (IPAddress?)null), CloudflareWatch.Decide(2, [open], open, true));
        Assert.Equal((true, false, (IPAddress?)open), CloudflareWatch.Decide(2, [open], shut, false));
        Assert.Equal((true, true, (IPAddress?)null), CloudflareWatch.Decide(2, [open, shut], open, true));
        Assert.Equal((true, false, (IPAddress?)null), CloudflareWatch.Decide(2, [open, shut], null, false));
        Assert.Equal((false, false, (IPAddress?)null), CloudflareWatch.Decide(2, [], null, false));
        Assert.Equal((true, false, (IPAddress?)null), CloudflareWatch.Decide(2, [], shut, true));
    }
}
