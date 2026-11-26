using Zephyron.Core.Updates;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>Что обновление убирает от прежней версии — и что не тронет никогда.</summary>
public sealed class StaleFilesTests
{
    private static string P(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    [Fact]
    public void Decide_RemovesOnlyShippedBeforeAndGoneNow()
    {
        var stale = StaleFiles.Decide(
            ["presets/old.txt", "presets/keep.txt", "engines/zapret/lua/gone.lua"],
            ["presets/keep.txt", "presets/new.txt"]);

        Assert.Equal(new[] { P("presets/old.txt"), P("engines/zapret/lua/gone.lua") }, stale);
    }

    [Fact]
    public void Decide_NeverTouchesUserDataEvenIfManifestNamesIt()
    {
        var stale = StaleFiles.Decide(
            ["config/zephyron.json", "config/subscriptions.json", "config/window.json", "config/rules.user.yaml",
             "config/rules.user.before-autopick.yaml", "runtime/x.json", "reports/r.txt", "../outside.txt",
             "config/rules.yaml", "config/catalog.yaml"],
            []);

        Assert.Equal(new[] { P("config/rules.yaml") }, stale);
    }

    [Fact]
    public void Decide_PathSeparatorsAndCaseDoNotHideAFileThatIsStillShipped()
    {
        Assert.Empty(StaleFiles.Decide([@"themes\Dark\theme.json"], ["themes/dark/theme.json"]));
    }

    [Fact]
    public void Legacy_TakesBuildLeftoversButNotMsquicNorUserFiles()
    {
        var stale = StaleFiles.Legacy(
            ["Zephyron.exe", "msquic.dll", "Zephyron.Core.dll", "Zephyron.pdb", "Zephyron.deps.json", "zy.exe",
             "runtimes/win-x64/native/e_sqlite3.dll", "engines/zapret/lists/zy-old.txt", "engines/zapret/lists/mine.txt",
             "config/lists/mine.txt", "presets/mine.txt"],
            ["Zephyron.exe", "msquic.dll"]);

        Assert.Equal(
            new[] { P("Zephyron.Core.dll"), P("Zephyron.pdb"), P("Zephyron.deps.json"), P("zy.exe"),
             P("runtimes/win-x64/native/e_sqlite3.dll"), P("engines/zapret/lists/zy-old.txt") },
            stale);
    }
}
