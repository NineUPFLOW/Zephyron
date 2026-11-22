namespace Zephyron.Zapret.Evolution;

/// <summary>
/// Пространство допустимых значений генов и интеллектуальная база синергетических стратегий десинхронизации.
/// </summary>
public sealed class GenePool
{
    /// <summary>Все уникальные наборы шагов десинка из пресетов и каталога рецептов.</summary>
    public IReadOnlyList<IReadOnlyList<string>> KnownRecipeSteps { get; init; } = [];

    /// <summary>
    /// Известные имена блобов ClientHello.
    /// </summary>
    public IReadOnlyList<string> KnownBlobs { get; init; } =
    [
        "tls_google", "tls_vk", "tls_4pda", "tls_sber", "tls_vk_kyber",
        "tls_deepseek", "tls_iana", "tls_max", "tls_teamspeak", "tls_steam_chat",
        "tls_steamstatic", "tls_awswaf", "tls_gosuslugi",
        "tls1", "tls2", "tls3", "tls4", "tls5", "tls6", "tls7",
        "tls8", "tls9", "tls10", "tls11", "tls12", "tls13", "tls14",
        "tls17", "tls18"
    ];

    /// <summary>Допустимые значения <c>--out-range</c>.</summary>
    public IReadOnlyList<string> OutRanges { get; init; } =
        ["-d4", "-d6", "-d8", "-d10", "-d12", "-d14"];

    /// <summary>Допустимые значения <c>--payload</c>.</summary>
    public IReadOnlyList<string> KnownPayloads { get; init; } =
        ["tls_client_hello", "http_req", "tls_client_hello,http_req"];

    /// <summary>Оптимальные позиции разрезания для multisplit / multidisorder.</summary>
    public IReadOnlyList<string> SmartSplitPositions { get; init; } =
    [
        "1", "midsld", "host+2", "endhost-2", "sld+2", "sniext+1", "1,midsld", "1,host+2"
    ];

    /// <summary>Эффективные размеры перекрытия seqovl.</summary>
    public IReadOnlyList<int> SmartSeqOverlaps { get; init; } =
        [1, 568, 681, 1100, 1400];

    /// <summary>
    /// Блобы, рекомендованные для конкретных доменов/сервисов.
    /// </summary>
    public static string GetRecommendedBlobForHost(string host, Random rng)
    {
        var lower = host.ToLowerInvariant();
        if (lower.Contains("youtube") || lower.Contains("googlevideo") || lower.Contains("google"))
            return rng.Next(2) == 0 ? "tls_google" : "tls_iana";

        if (lower.Contains("discord"))
            return rng.Next(3) switch { 0 => "tls_sber", 1 => "tls_max", _ => "tls_vk" };

        if (lower.Contains("steam"))
            return rng.Next(2) == 0 ? "tls_steam_chat" : "tls_steamstatic";

        if (lower.Contains("twitch"))
            return rng.Next(2) == 0 ? "tls_teamspeak" : "tls_google";

        if (lower.Contains("hetzner") || lower.Contains("7tv") || lower.Contains("cloudflare"))
            return rng.Next(2) == 0 ? "tls_4pda" : "tls_deepseek";

        if (lower.Contains("vk") || lower.Contains("mail.ru") || lower.Contains("gosuslugi"))
            return rng.Next(2) == 0 ? "tls_vk" : "tls_gosuslugi";

        return "tls_google";
    }

    /// <summary>
    /// Очищает шаг от опций, ломающих TLS-рукопожатие на публичных CDN (например, tcp_md5).
    /// </summary>
    public static string SanitizeStep(string step)
    {
        return step.Replace(":tcp_md5", "", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Возвращает специализированный список проверенных рецептов для заданной секции сервиса,
    /// предотвращая деградацию рабочих секций в несовместимые стратегии.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> GetRecipesForSection(string? sectionName)
    {
        if (string.IsNullOrWhiteSpace(sectionName))
            return KnownRecipeSteps;

        var lower = sectionName.ToLowerInvariant();

        if (lower.Contains("pass") || lower.Contains("исключен") || lower.Contains("netrogat"))
            return [["pass"]];

        if (lower.Contains("youtube") || lower.Contains("googlevideo") || lower.Contains("google"))
        {
            return
            [
                ["multisplit:pos=sniext+1,midsld:seqovl=652"],
                ["multidisorder:pos=1,host+2,sld+2,sld+5,sniext+1,sniext+2,endhost-2:seqovl=1"],
                ["fake:blob=tls_max:badsum:repeats=4", "multidisorder:pos=1:seqovl=681:seqovl_pattern=tls_max"],
                ["fake:blob=tls_google:tcp_ts=-1000:repeats=2", "multisplit:pos=1:seqovl=568:seqovl_pattern=tls_google"],
                ["fake:blob=tls_iana:tcp_ts=-1000:badsum:repeats=3", "multisplit:pos=midsld:seqovl=1"],
                ["white_sandwich:blob=tls_google:before=4:after=2:badsum:pos=1,midsld"],
                ["syndata:blob=tls_google", "multisplit:pos=1:seqovl=568"]
            ];
        }

        if (lower.Contains("discord"))
        {
            return
            [
                ["fake:blob=tls_max:count=6:badsum:pos=1,midsld", "multidisorder:pos=1,host+2,sld+2:seqovl=1"],
                ["fake:blob=tls_sber:tcp_ts=-600000:repeats=4", "multisplit:pos=1:seqovl=681:seqovl_pattern=tls_sber"],
                ["fake:blob=tls_vk:tcp_ts=-1000:badsum:repeats=2", "multisplit:pos=midsld:seqovl=1"],
                ["multidisorder:pos=1,host+2,sld+2:seqovl=1"]
            ];
        }

        return KnownRecipeSteps;
    }

    /// <summary>
    /// Собирает пул из всех активных секций пресетов и эталонных рецептов из каталога.
    /// </summary>
    public static GenePool FromPresets(IReadOnlyList<ZapretPreset> presets)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var recipes = new List<IReadOnlyList<string>>();

        // 1. Извлекаем проверенные рецепты из каталога RecipeCatalog
        foreach (var catalogRecipe in RecipeCatalog.All)
        {
            if (catalogRecipe.Steps.Count == 0 || catalogRecipe.Steps[0] == RecipeCatalog.Pass)
                continue;

            var sanitizedSteps = catalogRecipe.Steps.Select(SanitizeStep).ToList();
            var key = string.Join('\n', sanitizedSteps);
            if (seen.Add(key))
                recipes.Add(sanitizedSteps);
        }

        // 2. Добавляем рецепты из переданных пресетов
        foreach (var preset in presets)
        {
            foreach (var section in preset.ActiveSections)
            {
                if (section.DesyncRecipes.Count == 0)
                    continue;

                var sanitizedSteps = section.DesyncRecipes.Select(SanitizeStep).ToList();
                var key = string.Join('\n', sanitizedSteps);
                if (seen.Add(key))
                    recipes.Add(sanitizedSteps);
            }
        }

        // 3. Добавляем передовые синергетические связки (DPI Bypass Synergy Chains)
        string[][] advancedSynergies =
        [
            // YouTube / Google / Googlevideo: передовые комбинации без конфликтующих опций
            ["multisplit:pos=sniext+1,midsld:seqovl=652"],
            ["multidisorder:pos=1,host+2,sld+2,sld+5,sniext+1,sniext+2,endhost-2:seqovl=1"],
            ["fake:blob=tls_max:badsum:repeats=4", "multidisorder:pos=1:seqovl=681:seqovl_pattern=tls_max"],
            ["fake:blob=tls_google:tcp_ts=-1000:repeats=2", "multisplit:pos=1:seqovl=568:seqovl_pattern=tls_google"],
            ["fake:blob=tls_iana:tcp_ts=-1000:badsum:repeats=3", "multisplit:pos=midsld:seqovl=1"],
            ["white_sandwich:blob=tls_google:before=4:after=2:badsum:pos=1,midsld"],
            ["syndata:blob=tls_google", "multisplit:pos=1:seqovl=568"],
            // Discord: Fake + Multidisorder с badsum / seqovl
            ["fake:blob=tls_max:count=6:badsum:pos=1,midsld", "multidisorder:pos=1,host+2,sld+2:seqovl=1"],
            ["fake:blob=tls_sber:tcp_ts=-600000:repeats=4", "multisplit:pos=1:seqovl=681:seqovl_pattern=tls_sber"],
            ["fake:blob=tls_vk:tcp_ts=-1000:badsum:repeats=2", "multisplit:pos=midsld:seqovl=1"],
            // Cloudflare / Hetzner / 7tv: 4pda / DeepSeek
            ["fake:blob=tls_4pda:tcp_ts=-600000:repeats=4", "multisplit:pos=1:seqovl=568:seqovl_pattern=tls_4pda"],
            ["fake:blob=tls_deepseek:tcp_ts=-1000:repeats=2", "multisplit:pos=1:seqovl=1"],
            // Steam / Twitch: Steam Chat & Teamspeak
            ["fake:blob=tls_steam_chat:tcp_ts=-1000:repeats=2", "multisplit:pos=1:seqovl=1"],
            ["fake:blob=tls_teamspeak:tcp_ts=-1000:repeats=2", "multisplit:pos=midsld:seqovl=1"],
            // Мультидоменное расщепление хоста без tcp_md5 коллизий
            ["hostfakesplit_multi:hosts=google.com,vimeo.com:tcp_ts=-1000:repeats=2"],
            ["ipfrag2:filter_cutoff=d3", "multisplit:pos=1:seqovl=1"]
        ];

        foreach (var syn in advancedSynergies)
        {
            var key = string.Join('\n', syn);
            if (seen.Add(key))
                recipes.Add(syn);
        }

        return new GenePool { KnownRecipeSteps = recipes };
    }
}
