using System.Text;
using Zephyron.Core.Rules;

namespace Zephyron.Zapret;

/// <summary>
/// Сети, которые человек поставил «напрямую»: winws2 их не трогает ни в одной секции.
/// </summary>
/// <remarks>
/// <para>
/// «Напрямую» по именам выводит щит (<c>WinwsCommandLine.Shield</c>), но секции пресета
/// по адресам имени не спрашивают: game filter подделывает начало потока ко всему
/// <c>ipset-all</c>, «discord.com (IP fallback)» — к сетям Google, Cloudflare и AWS.
/// Жалоба 13.11 (Heroes of Newerth): с Zephyron на «напрямую» игра не подключается,
/// с закрытым Zephyron — подключается. Поэтому свои правила «напрямую» по адресам
/// (сети, списки адресов, адреса программ) получают <c>--ipset-exclude</c> в каждой секции.
/// </para>
/// <para>
/// Только правила человека: базовые «напрямую» (ipset-ru, Riot) уже выведены пресетом
/// и <see cref="UdpOffDesync"/>, а российские сети целиком в каждой секции стоили бы памяти.
/// </para>
/// <para>
/// Пустой список winws2 понимает как «все адреса» (проверено 10.11 для --ipset), поэтому
/// в файле всегда стоит заглушка из TEST-NET-3.
/// </para>
/// </remarks>
public static class DirectAddresses
{
    public static string DefaultPath => Path.Combine("runtime", "desync-direct.txt");

    public const string Placeholder = "203.0.113.254";

    /// <summary>Сети правил человека «напрямую» по адресам.</summary>
    public static IReadOnlyList<string> Choose(IEnumerable<UserRuleEntry> entries, string? zapretRoot)
    {
        var direct = entries.Where(e => e.Enabled && !e.Off && e.Mode == RoutingMode.Direct).ToList();

        var sources = direct
            .Where(e => e.Match is MatchKind.Ip or MatchKind.IpSet)
            .Select(e => e.Value)
            .ToList();

        // Адреса программы (Программы → «адреса», их пополняет «Наблюдение») — список имён
        // по форме, но в нём сети: берём только их.
        sources.AddRange(direct
            .Where(e => e.Match == MatchKind.HostList && e.Value.EndsWith(".addresses.txt", StringComparison.OrdinalIgnoreCase))
            .SelectMany(e => ProgramRoutes.Entries(e.Value)));

        return sources.Count == 0 ? [] : AddressListReader.Expand(sources, zapretRoot, out _);
    }

    /// <summary>Пишет сети; без них — одну заглушку.</summary>
    public static void Write(IReadOnlyList<string> networks, string? path = null)
    {
        var target = path ?? DefaultPath;

        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(target)) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            File.WriteAllLines(target, networks.Prepend(Placeholder), new UTF8Encoding(false));
        }
        catch (IOException)
        {
            // Не записалось — останется прежний список: секции просто не узнают новых сетей.
        }
    }

    /// <summary>Ключ для секции winws2.</summary>
    public static string Exclude(string path) => "--ipset-exclude=" + path.Replace('\\', '/');
}
