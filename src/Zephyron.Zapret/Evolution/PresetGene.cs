namespace Zephyron.Zapret.Evolution;

/// <summary>
/// Вектор мутируемых генов одной секции пресета с поддержкой интеллектуальной настройки параметров.
/// </summary>
public sealed record PresetGene
{
    /// <summary>Порты <c>--filter-tcp</c>; <c>null</c> — ключ не добавляется (например, для UDP секций).</summary>
    public string? TcpPorts { get; init; } = "80,443-65535";

    /// <summary>Порты <c>--filter-udp</c>; <c>null</c> — ключ не добавляется.</summary>
    public string? UdpPorts { get; init; }

    /// <summary>Значение <c>--out-range</c>.</summary>
    public string OutRange { get; init; } = "-d8";

    /// <summary>Шаги <c>--lua-desync</c> по порядку, без ключа.</summary>
    public IReadOnlyList<string> DesyncSteps { get; init; } = [];

    /// <summary>Имена блобов <c>--blob</c> по порядку, без ключа.</summary>
    public IReadOnlyList<string> BlobRefs { get; init; } = [];

    /// <summary>Путь к файлу <c>--ipset</c>; <c>null</c> — ключ не добавляется.</summary>
    public string? IpSetPath { get; init; }

    /// <summary>Значение <c>--payload</c>.</summary>
    public string Payload { get; init; } = "tls_client_hello,http_req";

    /// <summary>Число повторов для <c>send:repeats=N</c>.</summary>
    public int Repeats { get; init; } = 1;

    /// <summary>
    /// Разбирает секцию пресета в ген.
    /// </summary>
    public static PresetGene FromSection(ZapretSection section)
    {
        string outRange = "-d8";
        string? udpPorts = null;
        string? tcpPorts = null;
        string? ipSetPath = null;
        string payload = "tls_client_hello,http_req";
        int repeats = 1;

        foreach (var arg in section.RawArguments)
        {
            if (arg.StartsWith("--out-range=", StringComparison.OrdinalIgnoreCase))
            {
                var val = arg["--out-range=".Length..].Trim();
                if (val.Length > 0)
                    outRange = val;
            }
            else if (arg.StartsWith("--filter-tcp=", StringComparison.OrdinalIgnoreCase))
            {
                var val = arg["--filter-tcp=".Length..].Trim();
                if (val.Length > 0)
                    tcpPorts = val;
            }
            else if (arg.StartsWith("--filter-udp=", StringComparison.OrdinalIgnoreCase))
            {
                var val = arg["--filter-udp=".Length..].Trim();
                if (val.Length > 0)
                    udpPorts = val;
            }
            else if (arg.StartsWith("--ipset=", StringComparison.OrdinalIgnoreCase))
            {
                var val = arg["--ipset=".Length..].Trim();
                if (val.Length > 0)
                    ipSetPath = val;
            }
            else if (arg.StartsWith("--payload=", StringComparison.OrdinalIgnoreCase))
            {
                var val = arg["--payload=".Length..].Trim();
                if (val.Length > 0)
                    payload = val;
            }
        }

        // Если в исходной секции не было ни tcp, ни udp — считаем по умолчанию TCP
        if (tcpPorts is null && udpPorts is null)
            tcpPorts = "80,443-65535";

        var steps = section.DesyncRecipes.ToList();
        foreach (var step in steps)
        {
            var part = step.Split(':', StringSplitOptions.TrimEntries);
            foreach (var kv in part)
            {
                if (kv.StartsWith("repeats=", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(kv["repeats=".Length..], out int r)
                    && r >= 1)
                {
                    repeats = r;
                    break;
                }
            }
        }

        return new PresetGene
        {
            TcpPorts = tcpPorts,
            UdpPorts = udpPorts,
            OutRange = outRange,
            DesyncSteps = steps,
            BlobRefs = [],
            IpSetPath = ipSetPath,
            Payload = payload,
            Repeats = repeats,
        };
    }

    /// <summary>
    /// Интеллектуальная семантическая мутация гена с учётом контекста сервиса.
    /// </summary>
    public PresetGene Mutate(GenePool pool, Random rng, string? sectionName = null)
    {
        // Секции-исключения (pass) неприкосновенны — их мутация сломает доступ к RU-сервисам
        if (DesyncSteps.Count == 1 && string.Equals(DesyncSteps[0], "pass", StringComparison.OrdinalIgnoreCase))
            return this;

        int roll = rng.Next(6);
        return roll switch
        {
            0 => MutateDesyncSteps(pool, rng, sectionName),
            1 => MutateStepParameters(pool, rng),
            2 => MutateBlobRef(pool, rng, sectionName),
            3 => MutateOutRange(pool, rng),
            4 => MutateAddOrRemoveStep(pool, rng),
            _ => MutateRepeats(rng)
        };
    }

    /// <summary>
    /// Локальный поиск (микро-калибровка) параметров для финальной максимизации эффективности.
    /// </summary>
    public PresetGene FineTune(GenePool pool, Random rng)
    {
        // Секции-исключения (pass) не трогаем
        if (DesyncSteps.Count == 1 && string.Equals(DesyncSteps[0], "pass", StringComparison.OrdinalIgnoreCase))
            return this;

        // Не меняем сам рецепт, но оптимизируем out-range, repeats и позиции разреза
        var tuned = MutateOutRange(pool, rng);
        if (tuned.DesyncSteps.Count > 0)
            tuned = tuned.MutateStepParameters(pool, rng);

        return tuned;
    }

    private PresetGene MutateDesyncSteps(GenePool pool, Random rng, string? sectionName)
    {
        var candidatePool = pool.GetRecipesForSection(sectionName);
        if (candidatePool.Count == 0)
            return this;

        // Выбираем рецепт из специализированного пула для этого типа сервиса
        var chosenSteps = candidatePool[rng.Next(candidatePool.Count)].ToList();

        // Если известно имя хоста секции — адаптируем блоб в новом рецепте
        if (!string.IsNullOrEmpty(sectionName))
        {
            var recommendedBlob = GenePool.GetRecommendedBlobForHost(sectionName, rng);
            for (int i = 0; i < chosenSteps.Count; i++)
            {
                if (chosenSteps[i].Contains("blob="))
                {
                    chosenSteps[i] = ReplaceParam(chosenSteps[i], "blob", recommendedBlob);
                }
                if (chosenSteps[i].Contains("seqovl_pattern="))
                {
                    chosenSteps[i] = ReplaceParam(chosenSteps[i], "seqovl_pattern", recommendedBlob);
                }
            }
        }

        return this with { DesyncSteps = chosenSteps };
    }

    private PresetGene MutateStepParameters(GenePool pool, Random rng)
    {
        if (DesyncSteps.Count == 0)
            return this;

        int stepIdx = rng.Next(DesyncSteps.Count);
        string step = DesyncSteps[stepIdx];

        // 1. Мутация pos=...
        if (step.Contains("pos="))
        {
            var newPos = pool.SmartSplitPositions[rng.Next(pool.SmartSplitPositions.Count)];
            step = ReplaceParam(step, "pos", newPos);
        }

        // 2. Мутация seqovl=...
        if (step.Contains("seqovl="))
        {
            var newOvl = pool.SmartSeqOverlaps[rng.Next(pool.SmartSeqOverlaps.Count)];
            step = ReplaceParam(step, "seqovl", newOvl.ToString());
        }

        // 3. Мутация repeats=...
        if (step.Contains("repeats="))
        {
            int newRep = rng.Next(2, 6);
            step = ReplaceParam(step, "repeats", newRep.ToString());
        }

        var newSteps = DesyncSteps.ToList();
        newSteps[stepIdx] = step;
        return this with { DesyncSteps = newSteps };
    }

    private PresetGene MutateBlobRef(GenePool pool, Random rng, string? sectionName)
    {
        string blob = !string.IsNullOrEmpty(sectionName)
            ? GenePool.GetRecommendedBlobForHost(sectionName, rng)
            : pool.KnownBlobs[rng.Next(pool.KnownBlobs.Count)];

        // Если в шагах есть упоминание блоба, синхронизируем его
        var updatedSteps = DesyncSteps.Select(step =>
        {
            var s = step;
            if (s.Contains("blob="))
                s = ReplaceParam(s, "blob", blob);
            if (s.Contains("seqovl_pattern="))
                s = ReplaceParam(s, "seqovl_pattern", blob);
            return s;
        }).ToList();

        var newBlobs = BlobRefs.Count == 0 ? (IReadOnlyList<string>)[blob] : [blob];
        return this with { BlobRefs = newBlobs, DesyncSteps = updatedSteps };
    }

    private PresetGene MutateOutRange(GenePool pool, Random rng)
    {
        int idx = -1;
        for (int i = 0; i < pool.OutRanges.Count; i++)
        {
            if (string.Equals(pool.OutRanges[i], OutRange, StringComparison.OrdinalIgnoreCase))
            {
                idx = i;
                break;
            }
        }

        if (idx < 0)
            return this with { OutRange = pool.OutRanges[rng.Next(pool.OutRanges.Count)] };

        int delta = rng.Next(2) == 0 ? -1 : 1;
        int next = Math.Clamp(idx + delta, 0, pool.OutRanges.Count - 1);
        return this with { OutRange = pool.OutRanges[next] };
    }

    private PresetGene MutateAddOrRemoveStep(GenePool pool, Random rng)
    {
        if (DesyncSteps.Count == 0 || (DesyncSteps.Count < 4 && rng.Next(2) == 0))
        {
            if (pool.KnownRecipeSteps.Count == 0)
                return this;

            var bucket = pool.KnownRecipeSteps[rng.Next(pool.KnownRecipeSteps.Count)];
            var step = bucket[rng.Next(bucket.Count)];
            return this with { DesyncSteps = [.. DesyncSteps, step] };
        }
        else
        {
            int remove = rng.Next(DesyncSteps.Count);
            var newSteps = DesyncSteps.Where((_, i) => i != remove).ToList();
            return this with { DesyncSteps = newSteps };
        }
    }

    private PresetGene MutateRepeats(Random rng)
    {
        int delta = rng.Next(2) == 0 ? -1 : 1;
        int next = Math.Clamp(Repeats + delta, 1, 5);
        return this with { Repeats = next };
    }

    private static string ReplaceParam(string step, string paramName, string newValue)
    {
        var parts = step.Split(':').ToList();
        for (int i = 0; i < parts.Count; i++)
        {
            if (parts[i].StartsWith($"{paramName}=", StringComparison.OrdinalIgnoreCase))
            {
                parts[i] = $"{paramName}={newValue}";
                return string.Join(':', parts);
            }
        }
        return step;
    }

    /// <summary>
    /// Одноточечный кроссовер двух генов.
    /// </summary>
    public static PresetGene Crossover(PresetGene a, PresetGene b, Random rng) =>
        new()
        {
            TcpPorts    = rng.Next(2) == 0 ? a.TcpPorts    : b.TcpPorts,
            UdpPorts    = rng.Next(2) == 0 ? a.UdpPorts    : b.UdpPorts,
            OutRange    = rng.Next(2) == 0 ? a.OutRange    : b.OutRange,
            DesyncSteps = rng.Next(2) == 0 ? a.DesyncSteps : b.DesyncSteps,
            BlobRefs    = rng.Next(2) == 0 ? a.BlobRefs    : b.BlobRefs,
            IpSetPath   = rng.Next(2) == 0 ? a.IpSetPath   : b.IpSetPath,
            Payload     = rng.Next(2) == 0 ? a.Payload     : b.Payload,
            Repeats     = rng.Next(2) == 0 ? a.Repeats     : b.Repeats,
        };
}
