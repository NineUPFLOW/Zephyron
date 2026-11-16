namespace Zephyron.Zapret.Evolution;

/// <summary>
/// Особь-кандидат эволюционной популяции пресетов.
/// </summary>
/// <remarks>
/// Хранит геном в виде словаря: имя секции пресета -> мутируемый ген секции.
/// Умеет преобразовывать геном обратно в исполняемый <see cref="ZapretPreset"/>.
/// </remarks>
public sealed class PresetIndividual
{
    /// <summary>Уникальный идентификатор особи.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Номер поколения, в котором особь была рождена.</summary>
    public int GenerationNumber { get; init; }

    /// <summary>
    /// Геном особи: отображение имени секции на её ген.
    /// </summary>
    public required IReadOnlyDictionary<string, PresetGene> Genes { get; init; }

    /// <summary>Итоговый балл приспособленности (0.0..1.0); NaN — особь не оценена.</summary>
    public double FitnessScore { get; set; } = double.NaN;

    /// <summary>Число успешно прошедших сетевых проб.</summary>
    public int ProbesPassed { get; set; }

    /// <summary>Общее число выполненных сетевых проб.</summary>
    public int ProbesTotal { get; set; }

    /// <summary>Средняя задержка сетевого ответа в миллисекундах.</summary>
    public long AvgLatencyMs { get; set; }

    /// <summary>Результаты последних проб по хостам: хост -> успешно.</summary>
    public IReadOnlyDictionary<string, bool> LastHostOutcomes { get; set; } = new Dictionary<string, bool>();

    /// <summary>Родитель A (для кроссовера).</summary>
    public Guid? ParentA { get; init; }

    /// <summary>Родитель B (для кроссовера).</summary>
    public Guid? ParentB { get; init; }

    /// <summary>Была ли особь успешно оценена фитнес-функцией.</summary>
    public bool HasValidFitness => !double.IsNaN(FitnessScore);

    /// <summary>Человекочитаемая подпись фитнес-результата.</summary>
    public string FitnessLabel => HasValidFitness
        ? $"{FitnessScore:P0} ({ProbesPassed}/{ProbesTotal}, {AvgLatencyMs} мс)"
        : "не оценена";

    /// <summary>
    /// Создает особь из существующего пресета.
    /// </summary>
    public static PresetIndividual FromPreset(ZapretPreset preset, int generation = 0)
    {
        var genes = new Dictionary<string, PresetGene>(StringComparer.OrdinalIgnoreCase);

        foreach (var section in preset.ActiveSections)
        {
            if (string.IsNullOrWhiteSpace(section.Name))
                continue;

            genes[section.Name] = PresetGene.FromSection(section);
        }

        return new PresetIndividual
        {
            GenerationNumber = generation,
            Genes = genes,
        };
    }

    /// <summary>
    /// Собирает исполняемый пресет из генома на основе базового шаблона с сохранением всех специфичных флагов.
    /// </summary>
    public ZapretPreset ToPreset(ZapretPreset template)
    {
        var sections = new List<ZapretSection>();

        foreach (var original in template.Sections)
        {
            if (original.IsPassThrough || !Genes.TryGetValue(original.Name, out var gene))
            {
                sections.Add(original);
                continue;
            }

            var rawArgs = RebuildSectionArguments(original, gene);

            var builtSection = new ZapretSection
            {
                Name = original.Name,
                HostListPaths = original.HostListPaths,
                InlineDomains = original.InlineDomains,
                IpSetPaths = gene.IpSetPath != null ? [gene.IpSetPath] : original.IpSetPaths,
                DesyncRecipes = gene.DesyncSteps,
                IsPassThrough = false,
                RawArguments = rawArgs,
            };

            sections.Add(builtSection);
        }

        return new ZapretPreset
        {
            Name = $"{template.Name} [gen-{GenerationNumber}]",
            FilePath = template.FilePath,
            Description = $"Эволюционная модификация {template.Name} (поколение {GenerationNumber})",
            BuiltinVersion = template.BuiltinVersion,
            IconColor = template.IconColor,
            GlobalArguments = template.GlobalArguments,
            Sections = sections,
        };
    }

    /// <summary>
    /// Гармонизирует аргументы секции, обновляя мутированные гены и сохраняя кастомные фильтры.
    /// </summary>
    public static List<string> RebuildSectionArguments(ZapretSection original, PresetGene gene)
    {
        var rawArgs = new List<string>();
        bool handledOutRange = false;
        bool handledTcp = false;
        bool handledUdp = false;
        bool handledPayload = false;
        bool handledIpSet = false;

        foreach (var arg in original.RawArguments)
        {
            if (arg.StartsWith("--lua-desync=", StringComparison.OrdinalIgnoreCase))
            {
                // Пропускаем старые шаги desync — добавим новые в конце
                continue;
            }
            if (arg.StartsWith("--filter-tcp=", StringComparison.OrdinalIgnoreCase))
            {
                rawArgs.Add($"--filter-tcp={gene.TcpPorts}");
                handledTcp = true;
                continue;
            }
            if (arg.StartsWith("--filter-udp=", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(gene.UdpPorts))
                    rawArgs.Add($"--filter-udp={gene.UdpPorts}");
                handledUdp = true;
                continue;
            }
            if (arg.StartsWith("--out-range=", StringComparison.OrdinalIgnoreCase))
            {
                rawArgs.Add($"--out-range={gene.OutRange}");
                handledOutRange = true;
                continue;
            }
            if (arg.StartsWith("--payload=", StringComparison.OrdinalIgnoreCase))
            {
                rawArgs.Add($"--payload={gene.Payload}");
                handledPayload = true;
                continue;
            }
            if (arg.StartsWith("--ipset=", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(gene.IpSetPath))
                    rawArgs.Add($"--ipset={gene.IpSetPath}");
                handledIpSet = true;
                continue;
            }

            // Любые другие аргументы (--name, --wf-raw-part, --hostlist и т.д.) сохраняем без изменений!
            rawArgs.Add(arg);
        }

        // Если какого-то обязательного мутируемого флага не было в исходных RawArguments — добавляем
        if (!handledTcp)
            rawArgs.Insert(Math.Min(1, rawArgs.Count), $"--filter-tcp={gene.TcpPorts}");
        if (!handledUdp && !string.IsNullOrWhiteSpace(gene.UdpPorts))
            rawArgs.Add($"--filter-udp={gene.UdpPorts}");
        if (!handledOutRange)
            rawArgs.Add($"--out-range={gene.OutRange}");
        if (!handledPayload)
            rawArgs.Add($"--payload={gene.Payload}");
        if (!handledIpSet && !string.IsNullOrWhiteSpace(gene.IpSetPath))
            rawArgs.Add($"--ipset={gene.IpSetPath}");

        // Добавляем новые desync шаги
        foreach (var step in gene.DesyncSteps)
            rawArgs.Add($"--lua-desync={step}");

        return rawArgs;
    }
}
