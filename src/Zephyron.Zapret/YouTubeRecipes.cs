using Zephyron.Core;

namespace Zephyron.Zapret;

/// <summary>
/// Способы обхода для YouTube в пресете Zephyron Adaptive — по очереди, какой провайдер пропускает.
/// </summary>
/// <remarks>
/// <para>
/// Провайдеры меняют правила для YouTube по нескольку раз за день, и один рецепт
/// этого не переживает. Смену ведёт программа (сторож YouTube в окне): YouTube
/// перестал открываться — берётся следующий рецепт и обход перезапускается.
/// </para>
/// <para>
/// Не <c>circular</c> winws2 (15.11): в секции по именам он сбивал обход чужим
/// сайтам — firstlook.gg замерзал на 27 КБ 6 раз из 6, без него открывался 4 из 4,
/// в том числе когда circular видел только ClientHello.
/// </para>
/// <para>
/// Рецепты: свежий рецепт YouTube Zapret GUI 2.44 (15.11), прежний Universal V12 Lite,
/// плечи circular Zapret GUI и рецепт general Flowseal (MIT).
/// </para>
/// </remarks>
public static class YouTubeRecipes
{
    /// <summary>Секция страниц YouTube, рецепт которой меняется.</summary>
    public const string SectionName = "youtube.com (интерфейс)";

    /// <summary>Секция видеосерверов: страница открылась, а видео стоит — режут её, и рецепт меняется вместе.</summary>
    public const string VideoSectionName = "googlevideo.com (CDN сервера)";

    /// <summary>Куда уйти, когда в Adaptive не помог ни один рецепт: пресет для строгих провайдеров.</summary>
    public const string FallbackPreset = "Zephyron Strict";

    public static string StatePath => Path.Combine("runtime", "youtube-recipe.txt");

    public static readonly IReadOnlyList<string> Recipes =
    [
        // Первым — проверенный рецепт V12 Lite: свежий рецепт Zapret GUI (tls7) на сети владельца
        // 15.11 закрывал соседние имена Google (securetoken.googleapis.com — 0 из 3, с этим — 2 из 2).
        "multidisorder:pos=1,host+2,sld+2,sld+5,sniext+1,sniext+2,endhost-2:seqovl=1",
        "multisplit:pos=1:seqovl=681:seqovl_pattern=tls_google",
        "multisplit:pos=2:seqovl=681:seqovl_pattern=tls_google:repeats=8",
        "tls_multisplit_sni:seqovl=652:seqovl_pattern=tls_google",
        "multisplit:pos=2,midsld-2:seqovl=1:seqovl_pattern=tls7",
        "hostfakesplit_multi:hosts=google.com,vimeo.com:tcp_ts=-1000:tcp_md5:repeats=2",
    ];

    /// <summary>Смена рецепта — только в Zephyron Adaptive: в Strict и Universal рецепты авторов, их не трогаем.</summary>
    public static bool Applies(string? presetName) =>
        string.Equals(presetName, AppSettings.DefaultPresetName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Сколько помнить сменённый рецепт. Потом — снова первый: если он не откроет YouTube,
    /// сторож за пару проверок сменит его опять. Отчёт 23.11: Adaptive шёл со способом 2,
    /// выбранным ещё в прошлой версии (сторож тогда менял способы из-за ошибки), хотя
    /// у человека работает первый — на Strict и Stable YouTube открывался.
    /// </summary>
    public static readonly TimeSpan Memory = TimeSpan.FromHours(12);

    /// <summary>Номер выбранного рецепта; нет файла, мусор или выбран давно — первый.</summary>
    public static int Read(string? path = null, DateTime? now = null)
    {
        try
        {
            var file = path ?? StatePath;

            if ((now ?? DateTime.UtcNow) - File.GetLastWriteTimeUtc(file) > Memory)
                return 0;

            var text = File.ReadAllText(file).Trim();
            return int.TryParse(text, out int index) && index >= 0 && index < Recipes.Count ? index : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public static void Write(int index, string? path = null)
    {
        var target = path ?? StatePath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        AtomicFile.WriteAllText(target, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public static int Next(int index) => (index + 1) % Recipes.Count;

    /// <summary>
    /// Пресет с рецептом <paramref name="index"/> в секции YouTube: прежние шаги
    /// <c>--lua-desync</c> секции заменяются одним, на месте первого.
    /// </summary>
    public static ZapretPreset Apply(ZapretPreset preset, int index)
    {
        if (index < 0 || index >= Recipes.Count)
            index = 0;

        var recipe = Recipes[index];

        var sections = preset.Sections.Select(section =>
        {
            if (section.Name is not (SectionName or VideoSectionName))
                return section;

            int first = section.RawArguments.ToList().FindIndex(IsDesync);

            if (first < 0)
                return section;

            var raw = section.RawArguments.Where(a => !IsDesync(a)).ToList();
            raw.Insert(first, "--lua-desync=" + recipe);

            return section with { RawArguments = raw, DesyncRecipes = [recipe] };
        }).ToList();

        return preset with { Sections = sections };
    }

    private static bool IsDesync(string argument) => argument.StartsWith("--lua-desync=", StringComparison.Ordinal);
}
