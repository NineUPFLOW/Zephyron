using System.Text.Json;
using Xunit;
using Zephyron.Proxy;
using Zephyron.Subscriptions;

namespace Zephyron.Core.Tests;

public partial class WarpTests
{
    [Fact]
    public void Registration_without_detour_registration_goes_to_the_edges()
    {
        var root = Compile(new SingBoxOptions { WarpRegistrationEdges = WarpRegistration.Edges }, Warp.MasqueServer());

        var outbounds = root.GetProperty("outbounds").EnumerateArray().ToList();
        var masque = outbounds.First(o => o.GetProperty("tag").GetString() == Warp.MasqueTag);
        Assert.Equal(SingBoxOptions.WarpRegistrationTag, masque.GetProperty("profile").GetProperty("detour").GetString());

        var reg = outbounds.Single(o => o.GetProperty("tag").GetString() == SingBoxOptions.WarpRegistrationTag);
        Assert.Equal("direct", reg.GetProperty("type").GetString());
        Assert.Equal(SingBoxOptions.WarpRegistrationTag, reg.GetProperty("domain_resolver").GetString());

        foreach (var group in outbounds.Where(o => o.TryGetProperty("outbounds", out _)))
            Assert.DoesNotContain(group.GetProperty("outbounds").EnumerateArray(), m => m.GetString() == SingBoxOptions.WarpRegistrationTag);

        var hosts = root.GetProperty("dns").GetProperty("servers").EnumerateArray()
            .Single(s => s.GetProperty("tag").GetString() == SingBoxOptions.WarpRegistrationTag);
        Assert.Equal("hosts", hosts.GetProperty("type").GetString());
        Assert.Equal(WarpRegistration.Edges, hosts.GetProperty("predefined").GetProperty(WarpRegistration.Host).EnumerateArray().Select(e => e.GetString()!));
    }

    [Fact]
    public void Registration_without_edges_masque_is_left_as_before()
    {
        Assert.DoesNotContain(SingBoxOptions.WarpRegistrationTag, Compile(Warp.MasqueServer()).ToString());
    }
}
