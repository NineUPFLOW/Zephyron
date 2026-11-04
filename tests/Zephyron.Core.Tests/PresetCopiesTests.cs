using Zephyron.Core;
using Zephyron.Zapret;
using Xunit;

namespace Zephyron.Core.Tests;

public sealed class PresetCopiesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"zephyron-copies-{Guid.NewGuid():N}");

    public PresetCopiesTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private void Put(string file, params string[] lines) => File.WriteAllLines(Path.Combine(_dir, file), lines);

    [Fact]
    public void Duplicate_retitles_and_drops_builtin()
    {
        Put("A.txt", "# Preset: A", "# BuiltinVersion: 1.0", "--filter-tcp=443");
        var file = PresetCopies.Duplicate(_dir, "A.txt", "Моя");
        var text = File.ReadAllText(Path.Combine(_dir, file));
        Assert.Contains("# Preset: Моя", text);
        Assert.DoesNotContain("BuiltinVersion", text);
        Assert.Contains("--filter-tcp=443", text);
        Assert.True(PresetCopies.Shipped(Path.Combine(_dir, "A.txt")));
        Assert.False(PresetCopies.Shipped(Path.Combine(_dir, file)));
    }

    [Fact]
    public void CopyName_skips_taken()
    {
        Put("Копия A.txt", "x");
        Assert.Equal("Копия A (2)", PresetCopies.CopyName(_dir, "A"));
    }

    [Fact]
    public void Rename_refuses_shipped_and_moves_own()
    {
        Put("A.txt", "# Preset: A", "# BuiltinVersion: 1", "x");
        Assert.Throws<InvalidOperationException>(() => PresetCopies.Rename(_dir, "A.txt", "B"));
        Put("C.txt", "# Preset: C", "x");
        Assert.Equal("D.txt", PresetCopies.Rename(_dir, "C.txt", "D"));
        Assert.False(File.Exists(Path.Combine(_dir, "C.txt")));
        Assert.Contains("# Preset: D", File.ReadAllText(Path.Combine(_dir, "D.txt")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("a.")]
    public void Problem_rejects_bad_names(string name) =>
        Assert.NotNull(PresetCopies.Problem(name, _dir));

    [Fact]
    public void Renamed_moves_selection()
    {
        var s = new AppSettings { PresetName = "A" };
        Assert.Equal("B", PresetCopies.Renamed(s, "a", "B").PresetName);
        Assert.Equal("A", PresetCopies.Renamed(s, "Z", "B").PresetName);
    }
}
