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
/// QUIC, свой DNS, выключенный TLS 1.3, ручной прокси.
/// </summary>
/// <remarks>
/// <para>
/// 16.11: в Chrome и Edge YouTube стартовал по минуте — браузер ждал QUIC, который пресет
/// глушит; у пользователя Firefox не открывал YouTube вовсе — старый «твик» оставил ему
/// только TLS 1.2. Программа всё видела «доступным»: так ходят пробы, а не браузер.
/// </para>
/// <para>
/// ECH в Chromium не проверяется: в Chrome 157 и Edge флага для него больше нет (браузер
/// выбрасывает его при запуске, 16.11), а выключает его только политика организации.
/// </para>
/// <para>
/// Правка — только по кнопке человека и только у закрытого браузера: открытый перепишет
/// файл настроек при выходе. Перед правкой рядом кладётся копия «.before-zephyron».
/// </para>
/// </remarks>
public static class BrowserSetup
{
    private const string QuicOff = "enable-quic@2";

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

    /// <summary>Браузеры, которые нужно поправить и у которых сейчас открыто окно.</summary>
    public static IReadOnlyList<string> OpenToFix() =>
        Targets().Where(t => HasWindow(t.Process)).Select(t => t.Name).Distinct().ToList();

    /// <summary>
    /// Закрывает браузеры, правит их настройки и открывает заново — с теми же вкладками.
    /// </summary>
    /// <returns>Поправленные браузеры и те, что не закрылись.</returns>
    /// <remarks>
    /// Владелец 16.11: «браузеры должны автоматически закрыться, исправиться и открыться».
    /// Сначала окну предлагается закрыться самому (сессия сохраняется), через 10 с —
    /// принудительно; Chromium открывается с --restore-last-session, Firefox восстанавливает
    /// вкладки сам. Фоновый браузер без окон закрывается и заново не открывается.
    /// </remarks>
    public static (IReadOnlyList<string> Fixed, IReadOnlyList<string> Stuck) Fix()
    {
        var done = new List<string>();
        var stuck = new List<string>();

        foreach (var target in Targets().GroupBy(t => t.Process))
        {
            var first = target.First();
            var (closed, reopen) = Close(first.Process);

            if (!closed)
            {
                stuck.Add(first.Name);
                continue;
            }

            foreach (var t in target)
                t.Apply();

            done.Add(first.Name);

            if (reopen is { } exe)
                Reopen(exe, first.Process == "firefox" ? string.Empty : "--restore-last-session");
        }

        return (done, stuck);
    }

    /// <summary>Что править: браузер, его процесс и правка файлов.</summary>
    private static IEnumerable<(string Name, string Process, Action Apply)> Targets()
    {
        foreach (var (name, folder, policy, process) in Chromium)
        {
            if (LocalState(folder) is { } path && ChromiumIssues(name, Read(path), Policy(policy)).Any(i => i.Fixable))
            {
                yield return (name, process, () =>
                {
                    Backup(path);
                    File.WriteAllText(path, FixChromium(Read(path)));
                });
            }
        }

        foreach (var profile in FirefoxProfiles())
        {
            if (!FirefoxIssues(Read(Path.Combine(profile, "prefs.js")) + "\n" + Read(Path.Combine(profile, "user.js"))).Any(i => i.Fixable))
                continue;

            yield return ("Firefox", "firefox", () =>
            {
                foreach (var file in new[] { "prefs.js", "user.js" }.Select(f => Path.Combine(profile, f)).Where(File.Exists))
                {
                    Backup(file);
                    File.WriteAllText(file, FixFirefox(Read(file), isUserJs: file.EndsWith("user.js", StringComparison.OrdinalIgnoreCase)));
                }
            });
        }
    }

    internal static IEnumerable<BrowserIssue> ChromiumIssues(string name, string localState, IReadOnlyDictionary<string, object?> policy)
    {
        var flags = Flags(localState);

        if (!flags.Contains(QuicOff) && !Is(policy, "QuicAllowed", 0))
            yield return new(name, "включён QUIC — YouTube и сайты могут стартовать с задержкой", !policy.ContainsKey("QuicAllowed"));

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

    /// <summary>Local State с выключенным QUIC и без «безопасного DNS»; старая запись флага ECH убирается.</summary>
    internal static string FixChromium(string localState)
    {
        var root = JsonNode.Parse(localState)?.AsObject() ?? new JsonObject();
        var browser = root["browser"] as JsonObject ?? (JsonObject)(root["browser"] = new JsonObject());

        var flags = Flags(localState)
            .Where(f => !f.StartsWith("enable-quic@", StringComparison.Ordinal) && !f.StartsWith("encrypted-client-hello@", StringComparison.Ordinal))
            .Append(QuicOff);

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

    private static bool HasWindow(string process)
    {
        var all = Process.GetProcessesByName(process);

        try
        {
            return all.Any(p => p.MainWindowHandle != IntPtr.Zero);
        }
        finally
        {
            foreach (var p in all)
                p.Dispose();
        }
    }

    /// <summary>
    /// Закрывает браузер. <c>closed</c> — процессов не осталось; <c>reopen</c> — путь к
    /// программе, если у него было окно и его нужно открыть снова.
    /// </summary>
    /// <remarks>
    /// Фоновый браузер без окон (Edge с «Ускорением запуска», 16.11) закрывается молча:
    /// терять в нём нечего, а человеку его не закрыть.
    /// </remarks>
    private static (bool Closed, string? Reopen) Close(string process)
    {
        var all = Process.GetProcessesByName(process);

        try
        {
            if (all.Length == 0)
                return (true, null);

            bool windowed = all.Any(p => p.MainWindowHandle != IntPtr.Zero);
            string? exe = null;

            foreach (var p in all)
            {
                try
                {
                    exe ??= p.MainModule?.FileName;
                }
                catch (Exception)
                {
                    // Путь не читается у части служебных процессов — возьмём у другого.
                }

                if (p.MainWindowHandle != IntPtr.Zero)
                    p.CloseMainWindow();
            }

            var deadline = DateTime.UtcNow.AddSeconds(windowed ? 10 : 0);

            while (DateTime.UtcNow < deadline && Alive(process))
                Thread.Sleep(250);

            foreach (var p in Process.GetProcessesByName(process))
            {
                using (p)
                {
                    try
                    {
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                    catch (Exception)
                    {
                        // Уже вышел — проверим ниже.
                    }
                }
            }

            return (!Alive(process), windowed ? exe : null);
        }
        finally
        {
            foreach (var p in all)
                p.Dispose();
        }
    }

    private static bool Alive(string process)
    {
        var all = Process.GetProcessesByName(process);
        bool any = all.Length > 0;

        foreach (var p in all)
            p.Dispose();

        return any;
    }

    /// <summary>
    /// Открывает браузер от имени человека, а не от администратора.
    /// </summary>
    /// <remarks>
    /// Zephyron работает с правами администратора, и браузер, запущенный им напрямую,
    /// получил бы их тоже. Проводник запускает от обычного пользователя — через него,
    /// короткой командой (проводник сам аргументы не передаёт).
    /// </remarks>
    private static void Reopen(string exe, string arguments)
    {
        try
        {
            var script = Path.Combine(Path.GetTempPath(), $"zephyron-reopen-{Guid.NewGuid():N}.cmd");
            // cmd читает файл в OEM-кодировке: кириллица в имени пользователя
            // (C:\Users\Иван\...) без chcp 65001 превращалась в мусор. % в пути — удваиваем.
            static string Cmd(string s) => s.Replace("%", "%%");
            File.WriteAllText(script, $"@chcp 65001>nul\r\n@start \"\" \"{Cmd(exe)}\" {Cmd(arguments)}\r\n@del \"%~f0\"\r\n");
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { script }, UseShellExecute = false })?.Dispose();
        }
        catch (Exception)
        {
            // Не открылся — человек откроет сам; настройки уже поправлены.
        }
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
