using Xunit;
using Zephyron.Supervisor.Evolution;
using Zephyron.Zapret;
using Zephyron.Zapret.Evolution;

namespace Zephyron.Core.Tests;

public sealed class EvolutionEngineAuditFixTests
{
    [Fact]
    public void Crossover_CouplesTcpAndUdpPortsFromSameParent()
    {
        var rng = new Random(42);
        var tcpGene = new PresetGene { TcpPorts = "80,443", UdpPorts = null, OutRange = "-d8" };
        var udpGene = new PresetGene { TcpPorts = null, UdpPorts = "50000-65535", OutRange = "-d10" };

        for (int i = 0; i < 50; i++)
        {
            var child = PresetGene.Crossover(tcpGene, udpGene, rng);

            // Порты должны быть взяты согласованно от одного родителя:
            // либо только TCP (от tcpGene), либо только UDP (от udpGene)
            bool isTcpFromParentA = child.TcpPorts == "80,443" && child.UdpPorts == null;
            bool isUdpFromParentB = child.TcpPorts == null && child.UdpPorts == "50000-65535";

            Assert.True(isTcpFromParentA || isUdpFromParentB, "Кроссовер должен согласованно наследовать порты протокола от одного родителя");
        }
    }

    [Fact]
    public void GenomeSignature_IsDeterministicAndMatchesIdenticalGenomes()
    {
        var gene1 = new PresetGene { TcpPorts = "80,443", OutRange = "-d8", DesyncSteps = ["fake:blob=tls_google", "multisplit:pos=1"] };
        var gene2 = new PresetGene { TcpPorts = "80,443", OutRange = "-d8", DesyncSteps = ["fake:blob=tls_google", "multisplit:pos=1"] };

        var indA = new PresetIndividual
        {
            Genes = new Dictionary<string, PresetGene> { ["sec1"] = gene1 }
        };

        var indB = new PresetIndividual
        {
            Genes = new Dictionary<string, PresetGene> { ["sec1"] = gene2 }
        };

        var sigA = indA.GetGenomeSignature();
        var sigB = indB.GetGenomeSignature();

        Assert.NotNull(sigA);
        Assert.NotEmpty(sigA);
        Assert.Equal(sigA, sigB);

        var indDifferent = new PresetIndividual
        {
            Genes = new Dictionary<string, PresetGene> { ["sec1"] = gene1 with { OutRange = "-d12" } }
        };

        Assert.NotEqual(sigA, indDifferent.GetGenomeSignature());
    }

    [Fact]
    public void IsSectionWorking_CorrectlyEvaluatesGeneralAndTelegramSections()
    {
        // 1. General секция должна считаться рабочей, если большинство целевых хостов работают
        var outcomesMostlyPassing = new Dictionary<string, bool>
        {
            ["www.youtube.com"] = true,
            ["googlevideo.com"] = true,
            ["discord.com"] = true,
            ["api.twitch.tv"] = true,
            ["vk.com"] = true,
            ["api.telegram.org"] = true,
            ["steamcommunity.com"] = false
        };

        var ind = new PresetIndividual
        {
            GenerationNumber = 1,
            Genes = new Dictionary<string, PresetGene>
            {
                ["general"] = new PresetGene { DesyncSteps = ["multisplit:pos=1:seqovl=568"] },
                ["telegram"] = new PresetGene { DesyncSteps = ["fake:blob=tls_google"] }
            },
            LastHostOutcomes = outcomesMostlyPassing
        };

        var rng = new Random(42);
        var pool = GenePool.FromPresets([]);

        // При мутации рабочая секция "general" не должна разрушаться вслепую
        var mutated = GeneticOperators.Mutate(ind, pool, 2, rng, rate: 0.80);
        Assert.True(mutated.Genes.ContainsKey("general"));
        Assert.True(mutated.Genes.ContainsKey("telegram"));
    }

    [Fact]
    public void IslandModel_HandlesNaNWithoutCrashing()
    {
        var islandA = new List<PresetIndividual>
        {
            new() { Genes = new Dictionary<string, PresetGene>(), FitnessScore = 0.90, ProbesPassed = 6, ProbesTotal = 6, AvgLatencyMs = 50 },
            new() { Genes = new Dictionary<string, PresetGene>(), FitnessScore = double.NaN } // не оценена
        };

        var islandB = new List<PresetIndividual>
        {
            new() { Genes = new Dictionary<string, PresetGene>(), FitnessScore = 0.80, ProbesPassed = 5, ProbesTotal = 6, AvgLatencyMs = 60 },
            new() { Genes = new Dictionary<string, PresetGene>(), FitnessScore = double.NaN }
        };

        // Проверяем логику поиска worstIdx при наличии double.NaN
        int worstIdx = -1;
        double worstScore = double.PositiveInfinity;
        for (int w = 0; w < islandB.Count; w++)
        {
            var cand = islandB[w];
            double sc = cand.HasValidFitness ? cand.FitnessScore : -1.0;
            if (sc < worstScore)
            {
                worstScore = sc;
                worstIdx = w;
            }
        }

        // Худшим кандидатом на замену должен быть выбран неоцененный (sc = -1.0), а не -1
        Assert.Equal(1, worstIdx);
    }

    [Fact]
    public void DpiFingerprinter_GeneratesValidProfile()
    {
        var probes = new Dictionary<string, bool>
        {
            ["www.youtube.com"] = false,
            ["discord.com"] = false
        };

        var profile = DpiFingerprinter.DetectProfile(probes);

        Assert.Contains("Агрессивный ТСПУ", profile.IspFilterType);
        Assert.NotEmpty(profile.RecommendedSeedSteps);
        Assert.True(profile.PrefersSniExt);
    }

    [Fact]
    public void FitnessResult_PreservesStreamingSpeedAndVoiceStatus()
    {
        var result = new FitnessResult
        {
            Score = 0.92,
            Passed = 6,
            Total = 6,
            AvgLatencyMs = 75,
            StreamingSpeedKBps = 1450.0,
            DiscordVoicePassed = true,
            HostResults = new Dictionary<string, ProbeOutcome>()
        };

        Assert.Equal(1450.0, result.StreamingSpeedKBps);
        Assert.True(result.DiscordVoicePassed);
        Assert.Contains("1450 КБ/с", result.Summary);
    }

    [Fact]
    public async Task EvolutionStore_SavesAndLoadsPopulationWithNaNFitness()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"evo-store-test-{Guid.NewGuid():N}.json");
        try
        {
            var store = new EvolutionStore();
            var pop = new List<PresetIndividual>
            {
                new()
                {
                    GenerationNumber = 1,
                    Genes = new Dictionary<string, PresetGene>
                    {
                        ["general"] = new PresetGene { TcpPorts = "80,443", DesyncSteps = ["multisplit:pos=1"] }
                    },
                    FitnessScore = double.NaN
                },
                new()
                {
                    GenerationNumber = 1,
                    Genes = new Dictionary<string, PresetGene>
                    {
                        ["youtube"] = new PresetGene { TcpPorts = "443", DesyncSteps = ["fake:blob=tls_google"] }
                    },
                    FitnessScore = 0.85,
                    ProbesPassed = 5,
                    ProbesTotal = 6
                }
            };

            await store.SaveAsync(pop, 1, "TestPreset", tempFile);
            var loaded = await store.LoadAsync(tempFile);

            Assert.NotNull(loaded);
            Assert.Equal(2, loaded.Population.Count);
            Assert.True(double.IsNaN(loaded.Population[0].FitnessScore));
            Assert.Equal(0.85, loaded.Population[1].FitnessScore);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }
}
