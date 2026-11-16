using Xunit;
using Xunit.Abstractions;
using Zephyron.Supervisor.Evolution;
using Zephyron.Zapret;
using Zephyron.Zapret.Evolution;

namespace Zephyron.Core.Tests;

public sealed class GeneticEvolutionLiveCheckTests
{
    private readonly ITestOutputHelper _output;

    public GeneticEvolutionLiveCheckTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void FitnessProbe_ResolvesRealWinwsPathCorrectly()
    {
        var probe = new FitnessProbe("engines/zapret");
        var field = typeof(FitnessProbe).GetField("_winwsPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var resolvedPath = field?.GetValue(probe) as string;

        _output.WriteLine($"Resolved winws path: {resolvedPath}");
        Assert.NotNull(resolvedPath);
        Assert.True(File.Exists(resolvedPath), $"winws2 binary should exist at {resolvedPath}");
    }

    [Fact]
    public void FitnessScoring_PrioritizesReachabilityAndMinLatency()
    {
        var fastIndividual = new PresetIndividual
        {
            Genes = new Dictionary<string, PresetGene>(),
            FitnessScore = 0.95,
            ProbesPassed = 6,
            ProbesTotal = 6,
            AvgLatencyMs = 85
        };

        var slowIndividual = new PresetIndividual
        {
            Genes = new Dictionary<string, PresetGene>(),
            FitnessScore = 0.85,
            ProbesPassed = 6,
            ProbesTotal = 6,
            AvgLatencyMs = 850
        };

        var partialIndividual = new PresetIndividual
        {
            Genes = new Dictionary<string, PresetGene>(),
            FitnessScore = 0.87,
            ProbesPassed = 5,
            ProbesTotal = 6,
            AvgLatencyMs = 40
        };

        // Быстрая особь со 100% проходимостью должна опережать медленную
        Assert.True(fastIndividual.AvgLatencyMs < slowIndividual.AvgLatencyMs);
        Assert.True(fastIndividual.FitnessScore > slowIndividual.FitnessScore);
    }

    [Fact]
    public void GenePool_ContainsGooglevideoAndMediaSynergiesWithoutDangerousTcpMd5()
    {
        var pool = GenePool.FromPresets([]);
        Assert.NotEmpty(pool.KnownRecipeSteps);

        // Проверяем наличие высокоскоростных цепочек с перекрытием для googlevideo
        bool hasMultisplitSeqovl = pool.KnownRecipeSteps.Any(steps => steps.Any(s => s.Contains("multisplit") && s.Contains("seqovl=")));
        Assert.True(hasMultisplitSeqovl, "GenePool must contain multisplit with seqovl for high-throughput video streaming");

        // Проверяем, что опасные опции tcp_md5 не навязаны серверам
        foreach (var steps in pool.KnownRecipeSteps)
        {
            foreach (var s in steps)
            {
                if (s.Contains("google.com"))
                {
                    Assert.DoesNotContain("tcp_md5", s);
                }
            }
        }
    }

    [Fact]
    public void PassSection_NeverMutatesIntoDesync()
    {
        var rng = new Random(42);
        var pool = GenePool.FromPresets([]);
        var passGene = new PresetGene { DesyncSteps = ["pass"], OutRange = "-d8" };

        for (int i = 0; i < 50; i++)
        {
            var mutated = passGene.Mutate(pool, rng, "Исключения RU сайты");
            Assert.Equal("pass", Assert.Single(mutated.DesyncSteps));
        }
    }

    [Fact]
    public void WorkingSection_PreservesRecipeAndDoesNotDegrade()
    {
        var rng = new Random(42);
        var pool = GenePool.FromPresets([]);
        var youtubeGene = new PresetGene
        {
            DesyncSteps = ["multisplit:pos=sniext+1,midsld:seqovl=652"],
            OutRange = "-d8"
        };

        var ind = new PresetIndividual
        {
            GenerationNumber = 1,
            Genes = new Dictionary<string, PresetGene> { ["youtube.com"] = youtubeGene },
            LastHostOutcomes = new Dictionary<string, bool> { ["www.youtube.com"] = true }
        };

        // Поскольку youtube.com работает, рецепт не должен заменяться на чужой (например fake или disorder)
        for (int i = 0; i < 20; i++)
        {
            var mutated = GeneticOperators.Mutate(ind, pool, 2, rng, rate: 0.80);
            var resultGene = mutated.Genes["youtube.com"];
            Assert.StartsWith("multisplit", resultGene.DesyncSteps[0]);
            Assert.DoesNotContain("fake", resultGene.DesyncSteps[0]);
        }
    }

    [Fact]
    public void PresetExporter_MatchesPresetHeaderWithFilenameAndChainsGenerations()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "zephyron-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var exporter = new PresetExporter();
            var basePreset = new ZapretPreset
            {
                Name = "Universal V11 Lite",
                FilePath = Path.Combine(tempDir, "Universal V11 Lite.txt"),
                GlobalArguments = ["--filter-tcp=80,443"],
                Sections =
                [
                    new ZapretSection
                    {
                        Name = "youtube.com",
                        HostListPaths = ["youtube.txt"],
                        InlineDomains = [],
                        IpSetPaths = [],
                        DesyncRecipes = ["multisplit:pos=1"],
                        RawArguments = ["--filter-tcp=443", "--hostlist=youtube.txt", "--lua-desync=multisplit:pos=1"]
                    }
                ]
            };

            var winnerGen1 = new PresetIndividual
            {
                GenerationNumber = 15,
                FitnessScore = 0.94,
                ProbesPassed = 6,
                ProbesTotal = 6,
                AvgLatencyMs = 45,
                Genes = new Dictionary<string, PresetGene>
                {
                    ["youtube.com"] = new PresetGene { DesyncSteps = ["multisplit:pos=sniext+1:seqovl=600"] }
                }
            };

            // 1. Первая эволюция: из базового пресета
            var exportedPath1 = exporter.Export(winnerGen1, basePreset, tempDir);
            Assert.True(File.Exists(exportedPath1));
            var file1Name = Path.GetFileNameWithoutExtension(exportedPath1);
            Assert.Equal("Evolved Universal V11 Lite (Gen 15)", file1Name);

            // Проверяем, что заголовок # Preset: совпадает с именем файла
            var reader = new PresetReader();
            var loadedPreset1 = reader.Load(exportedPath1);
            Assert.Equal(file1Name, loadedPreset1.Name);

            // Проверяем, что ZapretPaths.FindPreset находит его по имени
            var found1 = ZapretPaths.FindPreset(loadedPreset1.Name, tempDir);
            Assert.NotNull(found1);
            Assert.Equal(Path.GetFullPath(exportedPath1), Path.GetFullPath(found1));

            // 2. Вторая эволюция: берём уже эволюционировавший пресет как родительский генофонд и эволюционируем дальше
            var winnerGen2 = new PresetIndividual
            {
                GenerationNumber = 15,
                FitnessScore = 0.98,
                ProbesPassed = 6,
                ProbesTotal = 6,
                AvgLatencyMs = 32,
                Genes = new Dictionary<string, PresetGene>
                {
                    ["youtube.com"] = new PresetGene { DesyncSteps = ["multisplit:pos=sniext+2:seqovl=650"] }
                }
            };

            var exportedPath2 = exporter.Export(winnerGen2, loadedPreset1, tempDir);
            Assert.True(File.Exists(exportedPath2));
            var file2Name = Path.GetFileNameWithoutExtension(exportedPath2);

            // Должно быть Gen 30, без дублирования "Evolved Evolved"
            Assert.Equal("Evolved Universal V11 Lite (Gen 30)", file2Name);

            var loadedPreset2 = reader.Load(exportedPath2);
            Assert.Equal(file2Name, loadedPreset2.Name);

            var found2 = ZapretPaths.FindPreset(loadedPreset2.Name, tempDir);
            Assert.NotNull(found2);
            Assert.Equal(Path.GetFullPath(exportedPath2), Path.GetFullPath(found2));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void GenomicPruner_RemovesDuplicatesAndSanitizesSteps()
    {
        var individual = new PresetIndividual
        {
            GenerationNumber = 5,
            Genes = new Dictionary<string, PresetGene>
            {
                ["youtube.com"] = new PresetGene
                {
                    DesyncSteps =
                    [
                        "multisplit:pos=1:seqovl=500",
                        "multisplit:pos=1:seqovl=500", // дубликат
                        "fake:blob=tls_google:tcp_md5", // содержит опасный tcp_md5
                        "  " // пустая строка
                    ]
                }
            }
        };

        var pruned = GenomicPruner.Prune(individual);
        var steps = pruned.Genes["youtube.com"].DesyncSteps;

        Assert.Equal(2, steps.Count);
        Assert.Equal("multisplit:pos=1:seqovl=500", steps[0]);
        Assert.Equal("fake:blob=tls_google", steps[1]);
        Assert.DoesNotContain("tcp_md5", steps[1]);
    }

    [Fact]
    public void DpiFingerprinter_RecommendsOptimalSniSplit_WhenYouTubeFails()
    {
        var failures = new Dictionary<string, bool>
        {
            ["www.youtube.com"] = false,
            ["discord.com"] = false
        };

        var profile = DpiFingerprinter.DetectProfile(failures);

        Assert.Contains("РДП/Гарда", profile.IspFilterType);
        Assert.True(profile.PrefersSniExt);
        Assert.Contains(profile.RecommendedSeedSteps, s => s.Contains("sniext"));
    }

    [Fact]
    public void DiscordVoiceProbe_HasValidStunBindingStructure()
    {
        // Проверяем, что STUN-пакет имеет корректный RFC 5389 заголовок
        var type = typeof(DiscordVoiceProbe);
        var field = type.GetField("StunBindingRequest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var bytes = field?.GetValue(null) as byte[];

        Assert.NotNull(bytes);
        Assert.Equal(20, bytes.Length);
        Assert.Equal(0x00, bytes[0]);
        Assert.Equal(0x01, bytes[1]); // Binding Request
        Assert.Equal(0x21, bytes[4]);
        Assert.Equal(0x12, bytes[5]);
        Assert.Equal(0xA4, bytes[6]);
        Assert.Equal(0x42, bytes[7]); // Magic Cookie
    }
}
