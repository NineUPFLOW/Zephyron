using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zephyron.Core.Rules;
using Zephyron.Core.Updates;

namespace Zephyron.Core;

/// <summary>
/// Копия личного состояния в одном файле <c>.zybackup</c> под паролем.
/// </summary>
/// <remarks>
/// <para>
/// В копии подписки, ключи и WARP-профиль — то есть секреты, поэтому файл шифруется
/// (AES-256-GCM, ключ из пароля через PBKDF2) и при любой порче читается как «неверный
/// пароль»: отличать одно от другого незачем и нельзя — это подсказка перебору.
/// </para>
/// <para>
/// Кэш движка и журналы не входят: это не состояние человека, а наживное.
/// Файл при разборе не доверяется: имена записей — по белому списку, размеры
/// считаются при чтении, любая лишняя запись отвергает весь файл.
/// </para>
/// </remarks>
public static class SettingsBackup
{
    public const int MinPassword = 8;

    public const string Extension = ".zybackup";

    public sealed record Restored(IReadOnlyList<string> Files, string BackupDir);

    /// <summary>Файл копии не открылся; сообщение — для человека.</summary>
    public sealed class BadBackupException(string message) : Exception(message);

    private const string Manifest = "backup.json";
    private const string OwnFolder = "config/lists/own/";
    private const int Iterations = 600_000;
    private const long MaxFile = 32L << 20;
    private const long MaxEntry = 8L << 20;
    private const long MaxTotal = 64L << 20;

    private static readonly byte[] Magic = "ZYB1"u8.ToArray();

    private static readonly string[] Fixed =
    [
        "config/zephyron.json",
        "config/rules.user.yaml",
        "config/subscriptions.json",
        "config/warp.conf",
        "config/warp.name",
        "config/dns.user.json",
        "config/addresses.yaml",
        "config/catalog.yaml",
        "config/pin-solutions.yaml",
        "runtime/desync-own.json",
        "runtime/desync-ipset-off.txt",
        "runtime/desync-udp-off.txt",
    ];

    private const string WrongOrDamaged = "Не удалось открыть копию: пароль неверный или файл повреждён.";

    public static void Export(string targetPath, string password, string? root = null)
    {
        if (password is null || password.Length < MinPassword)
            throw new ArgumentException($"Пароль короче {MinPassword} символов.", nameof(password));

        root = Path.GetFullPath(root ?? Directory.GetCurrentDirectory());

        var names = Fixed.Where(n => File.Exists(Locate(root, n))).ToList();
        var own = Path.Combine(root, "config", "lists", "own");

        if (Directory.Exists(own))
            names.AddRange(Directory.EnumerateFiles(own, "*.txt").Select(Path.GetFileName)
                .Where(n => IsSafeListName(n!)).Order(StringComparer.Ordinal).Select(n => OwnFolder + n));

        using var zip = new MemoryStream();

        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = JsonSerializer.Serialize(new
            {
                format = 1,
                app = UpdateCheck.Current,
                created = DateTime.UtcNow.ToString("o"),
                files = names,
            });

            using (var writer = archive.CreateEntry(Manifest).Open())
                writer.Write(Encoding.UTF8.GetBytes(manifest));

            foreach (var name in names)
            {
                using var source = new FileStream(Locate(root, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var entry = archive.CreateEntry(name).Open();
                source.CopyTo(entry);
            }
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        AtomicFile.WriteAllBytes(targetPath, Seal(zip.ToArray(), password));
    }

    /// <summary>Расшифрованная и проверенная копия: ещё ничего не записано.</summary>
    public sealed class Opened
    {
        internal Opened(Dictionary<string, byte[]> files) => Files = files;

        internal Dictionary<string, byte[]> Files { get; }
    }

    public static Restored Import(string sourcePath, string password, string? root = null) =>
        Apply(Read(sourcePath, password), root);

    /// <summary>
    /// Расшифровывает и проверяет копию, ничего не трогая: неверный пароль
    /// не должен останавливать движки (восстановление гасит их только перед записью).
    /// </summary>
    public static Opened Read(string sourcePath, string password)
    {
        if (new FileInfo(sourcePath).Length > MaxFile)
            throw new BadBackupException("Файл слишком большой для копии настроек Zephyron.");

        return new Opened(Unpack(Open(File.ReadAllBytes(sourcePath), password ?? "")));
    }

    public static Restored Apply(Opened opened, string? root = null)
    {
        root = Path.GetFullPath(root ?? Directory.GetCurrentDirectory());
        var files = opened.Files;

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupDir = Path.Combine(root, "runtime", "backups", "before-restore-" + stamp);

        // Прежнее — в сторону до первой записи: откат вручную возможен, пока цел хоть один файл.
        foreach (var name in files.Keys)
        {
            var current = Locate(root, name);

            if (!File.Exists(current))
                continue;

            var saved = Locate(backupDir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
            File.Copy(current, saved, overwrite: true);
        }

        foreach (var (name, bytes) in files)
        {
            var target = Locate(root, name);

            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new BadBackupException("Копия повреждена или не от Zephyron.");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            AtomicFile.WriteAllBytes(target, bytes);
        }

        return new Restored(files.Keys.ToList(), backupDir);
    }

    // Видимо для тестов: поддельный zip под настоящим паролем.
    internal static byte[] Seal(byte[] plain, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Derive(password, salt);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];

        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plain, cipher, tag, Magic);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return [.. Magic, .. salt, .. nonce, .. tag, .. cipher];
    }

    private static byte[] Open(byte[] file, string password)
    {
        if (file.Length < 4 || !file.AsSpan(0, 4).SequenceEqual(Magic))
            throw new BadBackupException("Это не копия настроек Zephyron или её версия не поддерживается.");

        if (file.Length < 4 + 16 + 12 + 16)
            throw new BadBackupException(WrongOrDamaged);

        var salt = file.AsSpan(4, 16);
        var nonce = file.AsSpan(20, 12);
        var tag = file.AsSpan(32, 16);
        var cipher = file.AsSpan(48);
        var plain = new byte[cipher.Length];
        var key = Derive(password, salt);

        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipher, tag, plain, Magic);
            return plain;
        }
        catch (CryptographicException)
        {
            throw new BadBackupException(WrongOrDamaged);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] Derive(string password, ReadOnlySpan<byte> salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);

    private static Dictionary<string, byte[]> Unpack(byte[] zipBytes)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool manifest = false;
        long total = 0;

        try
        {
            using var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);

            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName;

                if (!seen.Add(name) || (name != Manifest && !IsAllowed(name)))
                    throw new BadBackupException("Копия содержит лишнее и не будет восстановлена.");

                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;

                while ((read = stream.Read(chunk)) > 0)
                {
                    total += read;

                    if (buffer.Length + read > MaxEntry || total > MaxTotal)
                        throw new BadBackupException("Копия повреждена: файлы в ней слишком велики.");

                    buffer.Write(chunk, 0, read);
                }

                if (name == Manifest)
                {
                    manifest = true;

                    using var doc = JsonDocument.Parse(buffer.ToArray());

                    if (doc.RootElement.ValueKind != JsonValueKind.Object
                        || !doc.RootElement.TryGetProperty("format", out var format)
                        || format.ValueKind != JsonValueKind.Number || format.GetInt32() != 1)
                        throw new BadBackupException("Версия копии не поддерживается этой программой.");
                }
                else
                {
                    files[name] = buffer.ToArray();
                }
            }

            if (files.TryGetValue("config/zephyron.json", out var settings))
            {
                using var doc = JsonDocument.Parse(settings);

                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    throw new BadBackupException("Копия повреждена: настройки не читаются.");
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or FormatException or InvalidOperationException)
        {
            throw new BadBackupException("Копия повреждена и не будет восстановлена.");
        }

        if (!manifest || files.Count == 0)
            throw new BadBackupException("В копии нет файлов для восстановления.");

        return files;
    }

    private static bool IsAllowed(string name) =>
        Fixed.Contains(name, StringComparer.Ordinal)
        || (name.StartsWith(OwnFolder, StringComparison.Ordinal) && IsSafeListName(name[OwnFolder.Length..]));

    // Без разделителей, «..» и корня: имя из чужого файла не должно увести запись из папки списков.
    private static bool IsSafeListName(string name)
    {
        if (name.Length is 0 or > 100 || !name.EndsWith(".txt", StringComparison.Ordinal))
            return false;

        var stem = name[..^4];

        return stem.Trim('.').Length > 0
            && !stem.Contains("..", StringComparison.Ordinal)
            && !OwnLists.IsDeviceName(stem)
            && stem.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ' ');
    }

    private static string Locate(string root, string name) =>
        Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
}
