using System.Diagnostics;
using Zephyron.Core.Updates;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>
/// Подмена файлов окном вместо cmd — с копией прежних и откатом (владелец 12.10).
/// </summary>
public sealed class UpdateApplierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"zephyron-applier-{Guid.NewGuid():N}");

    private string Source => Path.Combine(_root, "Иван", "runtime", "update", "files", "Zephyron");
    private string Target => Path.Combine(_root, "Иван");
    private string Backup => Path.Combine(_root, "Иван", "runtime", "update", "backup");

    public UpdateApplierTests()
    {
        Write(Source, "a.txt", "новый");
        Write(Source, Path.Combine("engines", "b.txt"), "новый b");
        Write(Source, "Zephyron.exe", "новая программа");
        Write(Source, Path.Combine("config", "zephyron.json"), "{ \"из архива\": true }");

        Write(Target, "a.txt", "прежний");
        Write(Target, "Zephyron.exe", "прежняя программа");
        Write(Target, Path.Combine("config", "zephyron.json"), "{ \"мои настройки\": true }");

        UpdateApplier.Attempts = 2;
        UpdateApplier.Pause = TimeSpan.FromMilliseconds(50);
    }

    public void Dispose()
    {
        UpdateApplier.Attempts = 10;
        UpdateApplier.Pause = TimeSpan.FromSeconds(1);

        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static void Write(string root, string file, string text)
    {
        var path = Path.Combine(root, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private string Read(string file) => File.ReadAllText(Path.Combine(Target, file));

    [Fact]
    public async Task FilesAreReplacedAndPersonalOnesKept()
    {
        var applier = new UpdateApplier(Source, Target, Backup);

        Assert.Equal(3, applier.Total);
        Assert.Null(await applier.CopyAsync(null, CancellationToken.None));

        Assert.Equal("новый", Read("a.txt"));
        Assert.Equal("новый b", Read(Path.Combine("engines", "b.txt")));
        Assert.Equal("новая программа", Read("Zephyron.exe"));
        Assert.Equal("{ \"мои настройки\": true }", Read(Path.Combine("config", "zephyron.json")));
        Assert.Equal("прежний", File.ReadAllText(Path.Combine(Backup, "a.txt")));
    }

    [Fact]
    public async Task RollBackReturnsTheOldVersion()
    {
        var applier = new UpdateApplier(Source, Target, Backup);
        await applier.CopyAsync(null, CancellationToken.None);

        Assert.True(applier.CanRollBack);
        Assert.Equal(0, applier.RollBack());

        Assert.Equal("прежний", Read("a.txt"));
        Assert.Equal("прежняя программа", Read("Zephyron.exe"));
        Assert.False(File.Exists(Path.Combine(Target, "engines", "b.txt")));
        Assert.Equal("{ \"мои настройки\": true }", Read(Path.Combine("config", "zephyron.json")));
    }

    /// <summary>
    /// Занятый файл — сбой с его именем и тем, кто держит; программа остаётся
    /// прежней (её очередь последняя); повтор продолжает, не испортив копию прежнего.
    /// </summary>
    [Fact]
    public async Task LockedFileNamesItsHolderAndRetryResumes()
    {
        var applier = new UpdateApplier(Source, Target, Backup);
        ApplyFailure? failure;

        using (new FileStream(Path.Combine(Target, "a.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            failure = await applier.CopyAsync(null, CancellationToken.None);

        Assert.NotNull(failure);
        Assert.Equal("a.txt", failure.File);
        Assert.Contains(failure.Lockers, l => l.ProcessId == Environment.ProcessId);
        Assert.Equal("прежняя программа", Read("Zephyron.exe"));
        Assert.Contains("ERROR ", File.ReadAllText(Path.Combine(Target, UpdateInstaller.LogFile)));

        Assert.Null(await applier.CopyAsync(null, CancellationToken.None));
        Assert.Equal("новый", Read("a.txt"));
        Assert.Equal("прежний", File.ReadAllText(Path.Combine(Backup, "a.txt")));

        applier.RollBack();
        Assert.Equal("прежний", Read("a.txt"));
    }

    [Fact]
    public async Task AnExitedProcessIsNotWaitedFor()
    {
        using var finished = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!;
        finished.WaitForExit();

        Assert.True(await UpdateApplier.WaitForExitAsync(finished.Id, TimeSpan.FromSeconds(2), CancellationToken.None));
    }

    /// <summary>
    /// Обновление по описи: файл, который прежняя версия ставила, а новая — нет, уходит;
    /// свои файлы человека остаются; откат возвращает и файл, и прежнюю опись.
    /// </summary>
    [Fact]
    public async Task StaleShippedFilesAreRemovedAndRollbackRestoresThem()
    {
        Write(Target, Path.Combine("presets", "Old.txt"), "снятый пресет");
        Write(Target, Path.Combine("presets", "Mine.txt"), "свой пресет");
        Write(Target, StaleFiles.ManifestName, string.Join(Environment.NewLine, "a.txt", "Zephyron.exe", "presets/Old.txt"));
        Write(Source, StaleFiles.ManifestName, string.Join(Environment.NewLine, "a.txt", "Zephyron.exe", "engines/b.txt"));

        var applier = new UpdateApplier(Source, Target, Backup);
        Assert.Null(await applier.CopyAsync(null, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(Target, "presets", "Old.txt")));
        Assert.Equal("свой пресет", Read(Path.Combine("presets", "Mine.txt")));
        Assert.Contains("engines/b.txt", Read(StaleFiles.ManifestName));

        Assert.Equal(0, applier.RollBack());

        Assert.Equal("снятый пресет", Read(Path.Combine("presets", "Old.txt")));
        Assert.Contains("presets/Old.txt", Read(StaleFiles.ManifestName));
    }
}
