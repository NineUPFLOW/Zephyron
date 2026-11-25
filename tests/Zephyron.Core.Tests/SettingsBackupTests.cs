using System.IO.Compression;
using System.Text;
using Xunit;

namespace Zephyron.Core.Tests;

public sealed class SettingsBackupTests : IDisposable
{
    private const string Password = "correct horse";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"zephyron-backup-{Guid.NewGuid():N}");

    public SettingsBackupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string Put(string root, string name, string text)
    {
        var path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private string Source()
    {
        var root = Path.Combine(_dir, "src");
        Put(root, "config/zephyron.json", "{\"a\":1}");
        Put(root, "config/warp.conf", "secret");
        Put(root, "config/lists/own/mine.txt", "example.com");
        Put(root, "runtime/engine-cache.db", "cache");
        return root;
    }

    private string Pack(string root)
    {
        var file = Path.Combine(_dir, "b.zybackup");
        SettingsBackup.Export(file, Password, root);
        return file;
    }

    private static byte[] Zip(params (string Name, string Body)[] entries)
    {
        using var ms = new MemoryStream();

        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, body) in entries)
                using (var w = zip.CreateEntry(name).Open())
                    w.Write(Encoding.UTF8.GetBytes(body));

        return ms.ToArray();
    }

    private string Forged(params (string Name, string Body)[] entries)
    {
        var file = Path.Combine(_dir, "forged.zybackup");
        File.WriteAllBytes(file, SettingsBackup.Seal(Zip(entries), Password));
        return file;
    }

    [Fact]
    public void RoundTripRestoresFilesAndSkipsCache()
    {
        var file = Pack(Source());
        var target = Path.Combine(_dir, "dst");

        var restored = SettingsBackup.Import(file, Password, target);

        Assert.Equal("secret", File.ReadAllText(Path.Combine(target, "config", "warp.conf")));
        Assert.Equal("example.com", File.ReadAllText(Path.Combine(target, "config", "lists", "own", "mine.txt")));
        Assert.Contains("config/zephyron.json", restored.Files);
        Assert.False(File.Exists(Path.Combine(target, "runtime", "engine-cache.db")));
    }

    [Fact]
    public void RestoreRemovesBackupSetFilesAbsentFromTheBackup()
    {
        var src = Path.Combine(_dir, "src2");
        Put(src, "config/zephyron.json", "{}");
        var file = Pack(src);
        var target = Path.Combine(_dir, "dst2");
        var warp = Put(target, "config/warp.conf", "old");
        var list = Put(target, "config/lists/own/old.txt", "a.com");

        var restored = SettingsBackup.Import(file, Password, target);

        Assert.False(File.Exists(warp));
        Assert.False(File.Exists(list));
        Assert.Equal("old", File.ReadAllText(Path.Combine(restored.BackupDir, "config", "warp.conf")));
    }

    [Fact]
    public void WrongPasswordAndTamperingGiveTheSameMessage()
    {
        var file = Pack(Source());
        var wrong = Assert.Throws<SettingsBackup.BadBackupException>(() => SettingsBackup.Import(file, "another one", _dir));

        var bytes = File.ReadAllBytes(file);
        bytes[^1] ^= 1;
        File.WriteAllBytes(file, bytes);
        var torn = Assert.Throws<SettingsBackup.BadBackupException>(() => SettingsBackup.Import(file, Password, _dir));

        Assert.Equal(wrong.Message, torn.Message);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("config/lists/own/../../x.txt")]
    [InlineData("config/lists/own/a/b.txt")]
    [InlineData("runtime/engine-cache.db")]
    public void UnknownOrEscapingEntryRejectsWholeFile(string name)
    {
        var file = Forged(("backup.json", "{\"format\":1}"), ("config/warp.conf", "x"), (name, "x"));
        var target = Path.Combine(_dir, "dst");

        Assert.Throws<SettingsBackup.BadBackupException>(() => SettingsBackup.Import(file, Password, target));
        Assert.False(File.Exists(Path.Combine(target, "config", "warp.conf")));
    }

    [Fact]
    public void BrokenSettingsOrFormatIsRejected()
    {
        Assert.Throws<SettingsBackup.BadBackupException>(() => SettingsBackup.Import(
            Forged(("backup.json", "{\"format\":1}"), ("config/zephyron.json", "[1]")), Password, _dir));
        Assert.Throws<SettingsBackup.BadBackupException>(() => SettingsBackup.Import(
            Forged(("backup.json", "{\"format\":2}"), ("config/warp.conf", "x")), Password, _dir));
    }

    [Fact]
    public void ShortPasswordIsRejected() =>
        Assert.Throws<ArgumentException>(() => SettingsBackup.Export(Path.Combine(_dir, "x.zybackup"), "1234567", Source()));

    [Fact]
    public void ReplacedFilesAreCopiedBeforeRestore()
    {
        var file = Pack(Source());
        var target = Path.Combine(_dir, "dst");
        Put(target, "config/warp.conf", "old");

        var restored = SettingsBackup.Import(file, Password, target);

        Assert.Equal("old", File.ReadAllText(Path.Combine(restored.BackupDir, "config", "warp.conf")));
        Assert.Equal("secret", File.ReadAllText(Path.Combine(target, "config", "warp.conf")));
    }
}
