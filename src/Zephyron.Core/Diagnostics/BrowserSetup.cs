using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Zephyron.Core.Diagnostics;

/// <summary>Настройка браузера, которая мешает обходу.</summary>
/// <param name="Browser">Имя браузера для человека.</param>
/// <param name="What">Что не так — словами человека.</param>
/// <param name="Fixable">Программа может поправить сама (не политика организации).</param>
public sealed record BrowserIssue(string Browser, string What, bool Fixable);

/// <summary>
/// Настройки браузеров, из-за которых сайты открываются долго или не открываются вовсе:
/// QUIC, ECH, свой DNS, выключенный TLS 1.3, ручной прокси.
/// </summary>
/// <remarks>
/// <para>
/// 16.11: в Chrome и Edge YouTube стартовал по минуте — браузер ждал QUIC, который пресет
/// глушит; у пользователя Firefox не открывал YouTube вовсе — старый «твик» оставил ему
/// только TLS 1.2. Программа всё видела «доступным»: так ходят пробы, а не браузер.
/// </para>
/// <para>
/// ECH — по той же причине: ТСПУ режет приветствие с ECH к Cloudflare, и браузер, который
/// его шлёт, не открывает сайт, открытый для всех остальных.
/// </para>
/// <para>
/// Правка — только по кнопке человека и только у закрытого браузера: открытый перепишет
/// файл настроек при выходе. Перед правкой рядом кладётся копия «.before-zephyron».
/// </para>
/// </remarks>
public static class BrowserSetup
{
    private const string QuicOff = "enable-quic@2";
    private const string EchOff = "encrypted-client-hello@2";

    private static readonly (string Name, string Folder, string Policy, string Process)[] Chromium =
    [
        ("Chrome", @"Google\Chrome\User Data", @"SOFTWARE\Policies\Google\Chrome", "chrome"),
        ("Edge", @"Microsoft\Edge\User Data", @"SOFTWARE\Policies\Microsoft\Edge", "msedge"),
        ("Яндекс Браузер", @"Yandex\YandexBrowser\User Data", @"SOFTWARE\Policies\YandexBrowser", "browser"),
        ("Brave", @"BraveSoftware\Brave-Browser\User Data", @"SOFTWARE\Policies\BraveSoftware\Brave", "brave"),
        ("Vivaldi", @"Vivaldi\User Data", @"SOFTWARE\Policies\Vivaldi", "vivaldi"),
        ("Opera", @"Opera Software\Opera Stable", @"SOFTWARE\Policies\Opera Software\Opera", "opera"),
    ];

    /// <summary>Что мешает обходу в установленных браузерах.</summary>
    public static IReadOnlyList<BrowserIssue> Scan()
    {
        var issues = new List<BrowserIssue>();

        foreach (var (name, folder, policy, _) in Chromium)
        {
            if (LocalState(folder) is not { } path)
                continue;

            issues.AddRange(ChromiumIssues(name, Read(path), Policy(policy)));
        }

        foreach (var profile in FirefoxProfiles())
            issues.AddRange(FirefoxIssues(Read(Path.Combine(profile, "prefs.js")) + "\n" + Read(Path.Combine(profile, "user.js"))));

        // Одинаковые строки разных профилей Firefox — одной.
        return issues.Distinct().ToList();
    }

    /// <summary>
    /// Поправить всё, что можно, у закрытых браузеров.
    /// </summary>
    /// <returns>Поправленные браузеры и открытые — их нужно закрыть и нажать ещё раз.</returns>
    public static (IReadOnlyList<string> Fixed, IReadOnlyList<string> Running) Fix()
    {
        var done = new List<string>();
        var running = new List<string>();

        foreach (var (name, folder, policy, process) in Chromium)
        {
            if (LocalState(folder) is not { } path || !ChromiumIssues(name, Read(path), Policy(policy)).Any(i => i.Fixable))
                continue;

            if (IsRunning(process))
            {
                running.Add(name);
                continue;
            }

            Backup(path);
            File.WriteAllText(path, FixChromium(Read(path)));
            done.Add(name);
        }

        var firefox = FirefoxProfiles()
            .Where(p => FirefoxIssues(Read(Path.Combine(p, "prefs.js")) + "\n" + Read(Path.Combine(p, "user.js"))).Any(i => i.Fixable))
            .ToList();

        if (firefox.Count > 0 && IsRunning("firefox"))
        {
            running.Add("Firefox");
        }
        else if (firefox.Count > 0)
        {
            foreach (var profile in firefox)
            {
                foreach (var file in new[] { "prefs.js", "user.js" }.Select(f => Path.Combine(profile, f)).Where(File.Exists))
                {
                    Backup(file);
                    File.WriteAllText(file, FixFirefox(Read(file), isUserJs: file.EndsWith("user.js", StringComparison.OrdinalIgnoreCase)));
                }
            }

            done.Add("Firefox");
        }

        return (done, running);
    }

    internal static IEnumerable<BrowserIssue> ChromiumIssues(string name, string localState, IReadOnlyDictionary<string, object?> policy)
    {
        var flags = Flags(localState);

        if (!flags.Contains(QuicOff) && !Is(policy, "QuicAllowed", 0))
            yield return new(name, "включён QUIC — YouTube и сайты могут стартовать с задержкой", !policy.ContainsKey("QuicAllowed"));

        if (!flags.Contains(EchOff) && !Is(policy, "EncryptedClientHelloEnabled", 0))
            yield return new(name, "включён ECH — сайты на Cloudflare могут не открываться", !policy.ContainsKey("EncryptedClientHelloEnabled"));

        if (policy.TryGetValue("DnsOverHttpsMode", out var mode) ? Equals(mode, "secure") : BrowserDns.ChromiumSecure(localState) is not null)
            yield return new(name, "свой DNS («безопасный DNS») — обходит пины и маршруты Zephyron", !policy.ContainsKey("DnsOverHttpsMode"));

        if (Equals(policy.GetValueOrDefault("SSLVersionMax"), "tls1.2"))
            yield return new(name, "TLS 1.3 выключен политикой — часть сайтов не откроется", false);
    }

    internal static IEnumerable<BrowserIssue> FirefoxIssues(string prefs)
    {
        if (Pref(prefs, "security.tls.version.max") is { } max && int.TryParse(max, out int v) && v < 4)
            yield return new("Firefox", "выключен TLS 1.3 — YouTube и часть сайтов не откроются", true);

        if (Pref(prefs, "network.http.http3.enable") != "false")
            yield return new("Firefox", "включён QUIC (HTTP/3) — YouTube и сайты могут стартовать с задержкой", true);

        if (Pref(prefs, "network.trr.mode") is "2" or "3")
            yield return new("Firefox", "свой DNS («DNS через HTTPS») — обходит пины и маршруты Zephyron", true);

        // Ручной прокси не трогаем: он может быть нужен человеку. Только говорим.
        if (Pref(prefs, "network.proxy.type") == "1")
            yield return new("Firefox", "задан свой прокси в «Параметрах сети» — если он не работает, не откроется ничего", false);
    }

    /// <summary>Local State с выключенными QUIC и ECH и без «безопасного DNS».</summary>
    internal static string FixChromium(string localState)
    {
        var root = JsonNode.Parse(localState)?.AsObject() ?? new JsonObject();
        var browser = root["browser"] as JsonObject ?? (JsonObject)(root["browser"] = new JsonObject());

        var flags = Flags(localState)
            .Where(f => !f.StartsWith("enable-quic@", StringComparison.Ordinal) && !f.StartsWith("encrypted-client-hello@", StringComparison.Ordinal))
            .Append(QuicOff)
            .Append(EchOff);

        browser["enabled_labs_experiments"] = new JsonArray(flags.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray());

        if (root["dns_over_https"] is JsonObject doh && (string?)doh["mode"] == "secure")
            doh["mode"] = "off";

        return root.ToJsonString();
    }

    /// <summary>prefs.js или user.js без «твиков», которые ломают обход.</summary>
    internal static string FixFirefox(string text, bool isUserJs)
    {
        // TLS — назад к умолчанию (1.3): строку убираем, Firefox сам возьмёт своё.
        text = Regex.Replace(text, @"^\s*user_pref\(""security\.tls\.version\.max"",[^\n]*\n?", string.Empty, RegexOptions.Multiline);
        text = SetPref(text, "network.trr.mode", "5", onlyIfPresent: isUserJs);
        text = SetPref(text, "network.http.http3.enable", "false", onlyIfPresent: isUserJs);

        return text;
    }

    private static string SetPref(string text, string name, string value, bool onlyIfPresent)
    {
        var line = $"user_pref(\"{name}\", {value});";
        var pattern = $@"^\s*user_pref\(""{Regex.Escape(name)}"",[^\n]*$";

        if (Regex.IsMatch(text, pattern, RegexOptions.Multiline))
            return Regex.Replace(text, pattern, line, RegexOptions.Multiline);

        return onlyIfPresent ? text : text.TrimEnd('\r', '\n') + Environment.NewLine + line + Environment.NewLine;
    }

    /// <summary>Значение настройки Firefox; последняя запись побеждает (user.js читается после prefs.js).</summary>
    internal static string? Pref(string prefs, string name) =>
        Regex.Matches(prefs, $@"user_pref\(""{Regex.Escape(name)}"",\s*""?([^"")]*)""?\)")
            .LastOrDefault()?.Groups[1].Value.Trim();

    private static HashSet<string> Flags(string localState)
    {
        try
        {
            return (JsonNode.Parse(localState)?["browser"]?["enabled_labs_experiments"] as JsonArray)?
                .Select(n => (string?)n).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static bool Is(IReadOnlyDictionary<string, object?> policy, string key, int value) =>
        policy.TryGetValue(key, out var v) && v is int i && i == value;

    private static IReadOnlyDictionary<string, object?> Policy(string key)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var policy = hive.OpenSubKey(key);

                foreach (var name in policy?.GetValueNames() ?? [])
                    values[name] = policy!.GetValue(name);
            }
            catch (Exception)
            {
                // Ветка закрыта правами — политики не видно, судим по файлу.
            }
        }

        return values;
    }

    private static string? LocalState(string folder) =>
        new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData }
            .Select(f => Path.Combine(Environment.GetFolderPath(f), folder, "Local State"))
            .FirstOrDefault(File.Exists);

    private static IEnumerable<string> FirefoxProfiles()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Mozilla\Firefox\Profiles");

        return Directory.Exists(root)
            ? Directory.EnumerateDirectories(root).Where(d => File.Exists(Path.Combine(d, "prefs.js")))
            : [];
    }

    private static bool IsRunning(string process)
    {
        var all = Process.GetProcessesByName(process);
        bool any = all.Length > 0;

        foreach (var p in all)
            p.Dispose();

        return any;
    }

    private static void Backup(string path)
    {
        var copy = path + ".before-zephyron";

        // Первую копию не затираем: в ней — то, что было до программы.
        if (!File.Exists(copy))
            File.Copy(path, copy);
    }

    private static string Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
