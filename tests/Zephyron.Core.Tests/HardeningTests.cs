using Zephyron.Core;
using Zephyron.Core.Rules;
using Zephyron.Core.Updates;
using Zephyron.Subscriptions;
using Xunit;

namespace Zephyron.Core.Tests;

public sealed class HardeningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"zephyron-hard-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void OwnListsRejectTraversal()
    {
        Directory.CreateDirectory(Path.Combine(_root, "config", "lists", "own"));
        var outside = Path.Combine(_root, "config", "secret.txt");
        File.WriteAllText(outside, "x");

        Assert.False(OwnLists.IsOwn("config/lists/own/../../secret.txt"));
        Assert.Empty(OwnLists.Names("config/lists/own/../../secret.txt", _root));

        OwnLists.Delete("config/lists/own/../../secret.txt", _root);
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void ExpireBeyondRangeMeansNoExpiry()
    {
        var info = SubscriptionParser.BuildInfo([], [], "expire=99999999999999", null, null, null);
        Assert.Null(info.ExpiresAt);
    }

    private const string Conf = "[Interface]\nPrivateKey = a=\nAddress = 10.0.0.2 # mine\n\n[Peer]\nPublicKey = b=\nEndpoint = 1.2.3.4:51820 # srv\nPersistentKeepalive = {0}\n";

    [Theory]
    [InlineData("0", 0)]
    [InlineData("off", 0)]
    [InlineData("25", 25)]
    [InlineData("junk", 30)]
    public void WireGuardCommentsAndKeepalive(string keepalive, int expected)
    {
        Assert.True(WireGuardConf.TryParse(string.Format(Conf, keepalive), null, out var server, out var error), error);
        Assert.Equal("1.2.3.4", server!.Host);
        Assert.Equal(["10.0.0.2/32"], server.LocalAddresses);
        Assert.Equal(expected, server.KeepaliveSeconds);
    }

    [Fact]
    public void TrailingZerosDoNotMakeAnotherVersion()
    {
        Assert.False(UpdateCheck.IsNewer("1.2.0.0", "1.2.0"));
        Assert.False(UpdateCheck.IsNewer("1.2.0", "1.2.0.0"));
        Assert.True(UpdateCheck.IsNewer("1.2.1", "1.2.0.0"));
    }

    [Fact]
    public async Task SameNamedFileInAnotherFolderIsUpdated()
    {
        var source = Path.Combine(_root, "src");
        var target = Path.Combine(_root, "dst");
        foreach (var rel in new[] { Path.Combine("config", "zephyron.json"), Path.Combine("other", "zephyron.json") })
        {
            Directory.CreateDirectory(Path.Combine(source, Path.GetDirectoryName(rel)!));
            File.WriteAllText(Path.Combine(source, rel), "new");
            Directory.CreateDirectory(Path.Combine(target, Path.GetDirectoryName(rel)!));
            File.WriteAllText(Path.Combine(target, rel), "old");
        }

        Assert.Null(await new UpdateApplier(source, target, Path.Combine(_root, "bak")).CopyAsync(null, CancellationToken.None));
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "config", "zephyron.json")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "other", "zephyron.json")));
    }

    [Fact]
    public void BrokenSettingsAreCopiedAside()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "zephyron.json");
        File.WriteAllText(path, "{ broken");

        AppSettings.TryLoad(path, out _);

        Assert.Single(Directory.GetFiles(_root, "zephyron.json.bad-*"));
    }

    [Theory]
    [InlineData("https://github.com/NineUPFLOW/Zephyron/releases/tag/v1.4.0", "v1.4.0")]
    [InlineData("https://github.com/NineUPFLOW/Zephyron/releases/tag/1.2.0.1", "1.2.0.1")]
    [InlineData("https://github.com/NineUPFLOW/Zephyron/releases", null)]
    [InlineData("https://github.com/x/y/releases/tag/v1.4.0%2F..%2Fevil", null)]
    public void Latest_page_redirect_gives_the_tag(string location, string? tag) =>
        Assert.Equal(tag, Zephyron.Core.Updates.UpdateCheck.TagFrom(location));
}
