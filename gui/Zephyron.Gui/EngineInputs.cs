using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Zephyron.Core;
using Zephyron.Core.Rules;

namespace Zephyron.Gui;

/// <summary>
/// Отпечаток того, по чему работают движки: свои маршруты и настройки, влияющие на них.
/// </summary>
/// <remarks>
/// <para>
/// Предложение «перезапустить движки» прежде снималось проверкой «режим совпадает
/// с работающим» — а после смены маршрута, DNS или пула режим тот же, и метка
/// «Ждёт перезапуска» гасла через три секунды, хотя движки работали по старой
/// настройке (владелец 14.11: «логика какая-то неверная»). И наоборот: вернул
/// настройку как было — предложение висело.
/// </para>
/// <para>
/// Теперь при запуске движков отпечаток записывается (<see cref="Record"/>), а ждать
/// перезапуска есть чего, только пока нынешний отпечаток от него отличается
/// (<see cref="Differs"/>). Окно, вид и то, что движок меняет на лету (сервер выхода,
/// автоподбор сервера), в отпечаток не входят.
/// </para>
/// </remarks>
internal static class EngineInputs
{
    public static string DefaultPath => Path.Combine("runtime", "engine-inputs.txt");

    /// <summary>Поля настроек, которые движки не читают или меняют на лету.</summary>
    private static readonly HashSet<string> NotEngine = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowMaximized", "OfferDiscordRestart", "LearnDiscordVoice", "AutoInstallUpdates",
        "LearnRobloxServers", "LearnArcRaidersServers", "RouteFolders", "RouteOrder", "MadeFolders",
        "FolderLabels", "PreferredServer", "AutoPickExit", "ServerMemoryDays",
        "MeasureOnStart", "Theme", "ThemeBackgroundShown", "ThemeBlur", "ThemeDimSteps", "TrayBlur",
        "TrayDensity", "UiFont", "UiScale", "CheckForUpdates", "DismissedUpdate", "OnboardingDone",
        "SupportCardHidden", "Animations", "HomeFullWidth", "Effects", "HideOwnAddress", "LoadFailed",
        "VerifyTrafficReset", "GameFilterReset", "TelegramProxyAutoReset", "ProgramRulesRemoved",
        "RemovedProgramRules", "LastRunVersion",
        // Метки одноразовых миграций: сами по себе движки не меняют. LogsEnabled и AutoPickExcluded
        // движок читает (аргументы супервизора, DeadServerTags) — их тут нет намеренно.
        "PresetV11Lite", "PresetV12Lite", "PresetAdaptive", "WarpIsPath",
    };

    /// <summary>Отпечаток нынешних маршрутов и настроек.</summary>
    public static string Current(string? settingsPath = null, string? rulesPath = null)
    {
        var text = new StringBuilder();
        var rules = rulesPath ?? UserRulesFile.DefaultPath;

        if (File.Exists(rules))
            text.Append(File.ReadAllText(rules));

        text.Append('\n');

        var settings = settingsPath ?? AppSettings.DefaultPath;

        if (File.Exists(settings) && JsonNode.Parse(File.ReadAllText(settings)) is JsonObject json)
        {
            foreach (var (key, value) in json.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (!NotEngine.Contains(key))
                    text.Append(key).Append('=').Append(value?.ToJsonString()).Append('\n');
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>Записывает отпечаток, по которому движки сейчас поднимаются.</summary>
    public static void Record(string? path = null)
    {
        try
        {
            var target = path ?? DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            AtomicFile.WriteAllText(target, Current());
        }
        catch (Exception)
        {
            // Не записалось — Differs ответит «да», и предложение будет как прежде.
        }
    }

    /// <summary>
    /// Отличаются ли нынешние настройки от тех, с которыми подняты движки;
    /// отпечатка нет (движки подняты прежней версией) — считаем, что отличаются.
    /// </summary>
    public static bool Differs(string? path = null)
    {
        try
        {
            var target = path ?? DefaultPath;
            return !File.Exists(target) || File.ReadAllText(target).Trim() != Current();
        }
        catch (Exception)
        {
            return true;
        }
    }
}
