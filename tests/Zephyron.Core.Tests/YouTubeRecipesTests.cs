using Zephyron.Zapret;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>Способ обхода YouTube в нашем пресете: подстановка, память, круг.</summary>
public sealed class YouTubeRecipesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ytrecipe-" + Guid.NewGuid().ToString("N"));

    public YouTubeRecipesTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ZapretSection Section(string name, params string[] raw) => new()
    {
        Name = name,
        HostListPaths = [],
        InlineDomains = [],
        IpSetPaths = [],
        DesyncRecipes = raw.Where(a => a.StartsWith("--lua-desync=")).Select(a => a["--lua-desync=".Length..]).ToList(),
        RawArguments = raw,
    };

    private static ZapretPreset Preset(params ZapretSection[] sections) => new()
    {
        Name = "Zephyron Adaptive",
        FilePath = "x.txt",
        GlobalArguments = [],
        Sections = sections,
    };

    [Fact]
    public void TheChosenRecipeReplacesTheYouTubeSectionSteps()
    {
        var preset = Preset(
            Section(YouTubeRecipes.SectionName, "--filter-tcp=80,443", "--hostlist=lists/youtube.txt", "--out-range=-d8",
                "--lua-desync=multisplit:pos=2", "--lua-desync=extra:x=1", "--new-tail=1"),
            Section("Google (сервисы)", "--hostlist=lists/google.txt", "--lua-desync=fake:blob=tls_google"),
            Section(YouTubeRecipes.VideoSectionName, "--hostlist=lists/googlevideo.txt", "--lua-desync=multisplit:pos=2"));

        var applied = YouTubeRecipes.Apply(preset, 2);
        var youtube = applied.Sections[0];

        Assert.Equal(["--hostlist=lists/googlevideo.txt", "--lua-desync=" + YouTubeRecipes.Recipes[2]], applied.Sections[2].RawArguments);

        Assert.Equal(
            ["--filter-tcp=80,443", "--hostlist=lists/youtube.txt", "--out-range=-d8", "--lua-desync=" + YouTubeRecipes.Recipes[2], "--new-tail=1"],
            youtube.RawArguments);
        Assert.Equal([YouTubeRecipes.Recipes[2]], youtube.DesyncRecipes);
        Assert.Same(preset.Sections[1], applied.Sections[1]);
    }

    [Fact]
    public void ABadIndexFallsBackToTheFirstRecipe()
    {
        var preset = Preset(Section(YouTubeRecipes.SectionName, "--lua-desync=old"));

        Assert.Equal("--lua-desync=" + YouTubeRecipes.Recipes[0], YouTubeRecipes.Apply(preset, 99).Sections[0].RawArguments[0]);
    }

    [Fact]
    public void TheChoiceIsRememberedAndGoesRound()
    {
        var path = Path.Combine(_dir, "runtime", "youtube-recipe.txt");

        Assert.Equal(0, YouTubeRecipes.Read(path));

        YouTubeRecipes.Write(3, path);
        Assert.Equal(3, YouTubeRecipes.Read(path));

        File.WriteAllText(path, "мусор");
        Assert.Equal(0, YouTubeRecipes.Read(path));

        Assert.Equal(0, YouTubeRecipes.Next(YouTubeRecipes.Recipes.Count - 1));
    }

    [Theory]
    [InlineData("Zephyron Adaptive", true)]
    [InlineData("Universal V12 Lite", false)]
    [InlineData("Zephyron Strict", false)]
    [InlineData(null, false)]
    public void OnlyOurPresetChangesRecipes(string? name, bool expected) =>
        Assert.Equal(expected, YouTubeRecipes.Applies(name));

    [Fact]
    public void TheShippedPresetHasTheSectionWithTheFirstRecipe()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);

        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Zephyron.sln")))
            root = root.Parent;

        if (root is null)
            return;

        var preset = new PresetReader().Load(Path.Combine(root.FullName, "presets", "Zephyron Adaptive.txt"));
        var section = Assert.Single(preset.Sections, s => s.Name == YouTubeRecipes.SectionName);

        Assert.Equal([YouTubeRecipes.Recipes[0]], section.DesyncRecipes);
        Assert.Equal([YouTubeRecipes.Recipes[0]], Assert.Single(preset.Sections, s => s.Name == YouTubeRecipes.VideoSectionName).DesyncRecipes);
        Assert.DoesNotContain(preset.Sections.SelectMany(s => s.RawArguments), a => a.Contains("circular", StringComparison.Ordinal));
    }
}
