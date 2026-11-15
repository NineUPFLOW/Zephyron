using System.IO;
using Xunit;

namespace Zephyron.Gui.Tests;

/// <summary>Чистятся только папки кеша, и только трёх вариантов Discord.</summary>
public sealed class DiscordCacheTests
{
    [Fact]
    public void FoldersAreOnlyCacheOnes()
    {
        var folders = DiscordCache.Folders(@"C:\A");

        Assert.Equal(9, folders.Count);
        Assert.Contains(@"C:\A\discordptb\Code Cache", folders);
        Assert.DoesNotContain(folders, f => f.Contains("Local Storage") || f.EndsWith("discord"));
    }

    [Fact]
    public async Task ClearKeepsOtherFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), "zy-dc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "discord", "Cache", "sub"));
        Directory.CreateDirectory(Path.Combine(root, "discord", "Local Storage"));
        File.WriteAllText(Path.Combine(root, "discord", "Cache", "sub", "a"), "x");
        File.WriteAllText(Path.Combine(root, "discord", "Local Storage", "keep"), "x");

        try
        {
            var text = await DiscordCache.ClearAsync(root, close: false);

            Assert.Contains("1", text);
            Assert.False(File.Exists(Path.Combine(root, "discord", "Cache", "sub", "a")));
            Assert.True(File.Exists(Path.Combine(root, "discord", "Local Storage", "keep")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
