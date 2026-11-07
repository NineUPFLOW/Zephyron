using Xunit;
using Zephyron.Zapret;

namespace Zephyron.Core.Tests;

public class PresetWithOwnTests
{
    [Fact]
    public void Own_recipes_go_before_the_first_profile_with_copied_lists()
    {
        var root = Directory.CreateTempSubdirectory("zy-own-").FullName;

        try
        {
            var presets = Path.Combine(root, "presets");
            var lists = Path.Combine(root, "lists");
            Directory.CreateDirectory(presets);

            File.WriteAllLines(Path.Combine(presets, "Base.txt"),
            [
                "# Preset: Base",
                "# BuiltinVersion: 2.26",
                "--wf-tcp-out=443",
                "",
                "--name=youtube",
                "--filter-tcp=443",
                "--hostlist=lists/youtube.txt",
                "--lua-desync=pass",
            ]);

            var hosts = Path.Combine(root, "own.txt");
            File.WriteAllLines(hosts, ["example.com"]);

            var file = PresetCopies.WithOwn(presets, lists, "Base.txt", "Мой подбор",
            [
                new OwnDesyncProfile { Name = "split", Steps = ["multisplit:pos=1"], HostListPath = hosts, Ports = "443" },
            ]);

            var text = File.ReadAllLines(Path.Combine(presets, file)).ToList();

            Assert.Equal("# Preset: Мой подбор", text[0]);
            Assert.DoesNotContain(text, l => l.StartsWith("# BuiltinVersion", StringComparison.Ordinal));
            Assert.True(text.IndexOf("--lua-desync=multisplit:pos=1") < text.IndexOf("--name=youtube"));
            Assert.True(text.IndexOf("--new") < text.IndexOf("--name=youtube"));
            Assert.True(text.IndexOf("--wf-tcp-out=443") < text.IndexOf("--name=Подбор: split"));
            Assert.Equal(["example.com"], File.ReadAllLines(Path.Combine(lists, "zy-own-мойподбор-1.txt")));
            Assert.Contains("--hostlist=lists/zy-own-мойподбор-1.txt", text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Nothing_picked_is_refused()
    {
        var root = Directory.CreateTempSubdirectory("zy-own-").FullName;

        try
        {
            File.WriteAllLines(Path.Combine(root, "Base.txt"), ["# Preset: Base", "--name=x"]);

            Assert.Throws<InvalidOperationException>(() => PresetCopies.WithOwn(root, root, "Base.txt", "Пусто", []));
            Assert.False(File.Exists(Path.Combine(root, "Пусто.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
