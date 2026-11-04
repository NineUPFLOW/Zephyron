using System.IO.Compression;
using Zephyron.Core.Rules;
using Xunit;

namespace Zephyron.Core.Tests;

public sealed class RouteBundleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"zephyron-bundle-{Guid.NewGuid():N}");

    public RouteBundleTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    private string Rules(string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "rules.user.yaml");
        var file = UserRulesFile.Load(path);
        file.Set(MatchKind.HostList, OwnLists.PathFor("mine"), RoutingMode.Direct);
        file.Save();
        return path;
    }

    [Fact]
    public void Lists_travel_with_the_rules_and_existing_ones_stay()
    {
        var from = Path.Combine(_root, "a");
        var rules = Rules(from);
        OwnLists.Create("mine", ["example.com"], from);

        var bundle = Path.Combine(_root, "x" + RouteBundle.Extension);
        Assert.Equal(1, RouteBundle.Write(rules, bundle, from));
        Assert.True(RouteBundle.IsBundle(bundle));
        Assert.False(RouteBundle.IsBundle(rules));
        Assert.Single(RouteBundle.Read(bundle));
        Assert.Equal(["mine"], RouteBundle.ListNames(bundle));

        var to = Path.Combine(_root, "b");
        Assert.Equal((1, 0), RouteBundle.ImportLists(bundle, to));
        Assert.Equal(["example.com"], OwnLists.Names(OwnLists.PathFor("mine"), to));
        Assert.Equal((0, 1), RouteBundle.ImportLists(bundle, to));
    }

    [Fact]
    public void Plain_yaml_reads_as_before_and_unsafe_names_are_ignored()
    {
        var rules = Rules(Path.Combine(_root, "c"));
        Assert.Single(RouteBundle.Read(rules));
        Assert.Empty(RouteBundle.ListNames(rules));

        var evil = Path.Combine(_root, "evil.zyroutes");
        using (var zip = ZipFile.Open(evil, ZipArchiveMode.Create))
            zip.CreateEntry("own/../../x.txt");

        Assert.Empty(RouteBundle.ListNames(evil));
        Assert.Equal((0, 0), RouteBundle.ImportLists(evil, Path.Combine(_root, "d")));
    }
}
