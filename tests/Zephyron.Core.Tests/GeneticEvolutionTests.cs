using Xunit;
using Zephyron.Zapret;
using Zephyron.Zapret.Evolution;

namespace Zephyron.Core.Tests;

public sealed class GeneticEvolutionTests
{
    [Fact]
    public void FromPreset_ExtractsAllActiveSectionsAsGenes()
    {
        var section1 = new ZapretSection
        {
            Name = "discord.com",
            HostListPaths = ["lists/discord.txt"],
            InlineDomains = ["discord.com"],
            IpSetPaths = [],
            DesyncRecipes = ["multisplit:pos=1:seqovl=568"],
            IsPassThrough = false,
            RawArguments = ["--name=discord.com", "--filter-tcp=80,443", "--out-range=-d8", "--lua-desync=multisplit:pos=1:seqovl=568"]
        };

        var preset = new ZapretPreset
        {
            Name = "TestPreset",
            FilePath = "test.txt",
            GlobalArguments = ["--ctrack-disable=0"],
            Sections = [section1]
        };

        var ind = PresetIndividual.FromPreset(preset, generation: 0);

        Assert.Single(ind.Genes);
        Assert.True(ind.Genes.ContainsKey("discord.com"));
        var gene = ind.Genes["discord.com"];
        Assert.Equal("-d8", gene.OutRange);
        Assert.Equal("80,443", gene.TcpPorts);
        Assert.Single(gene.DesyncSteps);
    }

    [Fact]
    public void Crossover_CombinesParentsGenomes()
    {
        var rng = new Random(42);
        var geneA = new PresetGene { OutRange = "-d8", TcpPorts = "80,443", DesyncSteps = ["fake:blob=tls_google"] };
        var geneB = new PresetGene { OutRange = "-d10", TcpPorts = "443-65535", DesyncSteps = ["multisplit:pos=1"] };

        var parent1 = new PresetIndividual
        {
            GenerationNumber = 1,
            Genes = new Dictionary<string, PresetGene> { ["sec1"] = geneA }
        };

        var parent2 = new PresetIndividual
        {
            GenerationNumber = 1,
            Genes = new Dictionary<string, PresetGene> { ["sec1"] = geneB }
        };

        var (child1, child2) = GeneticOperators.Crossover(parent1, parent2, generation: 2, rng, rate: 1.0);

        Assert.Equal(2, child1.GenerationNumber);
        Assert.Equal(2, child2.GenerationNumber);
        Assert.Equal(parent1.Id, child1.ParentA);
        Assert.Equal(parent2.Id, child1.ParentB);
        Assert.True(child1.Genes.ContainsKey("sec1"));
    }

    [Fact]
    public void TournamentSelect_PicksFittestIndividual()
    {
        var rng = new Random(42);
        var indLow = new PresetIndividual
        {
            Genes = new Dictionary<string, PresetGene>(),
            FitnessScore = 0.20
        };

        var indHigh = new PresetIndividual
        {
            Genes = new Dictionary<string, PresetGene>(),
            FitnessScore = 0.95
        };

        var pop = new List<PresetIndividual> { indLow, indHigh };

        var selected = GeneticOperators.TournamentSelect(pop, rng, k: 2);
        Assert.Equal(0.95, selected.FitnessScore);
    }

    [Fact]
    public void AdaptiveMutation_IncreasesRateWhenStagnant()
    {
        var pop = new List<PresetIndividual>
        {
            new() { Genes = new Dictionary<string, PresetGene>(), FitnessScore = 0.81 },
            new() { Genes = new Dictionary<string, PresetGene>(), FitnessScore = 0.82 },
            new() { Genes = new Dictionary<string, PresetGene>(), FitnessScore = 0.815 }
        };

        double baseRate = 0.15;
        double adaptive = GeneticOperators.AdaptiveMutationRate(pop, baseRate);

        Assert.True(adaptive > baseRate);
    }

    [Fact]
    public void ToPreset_ReconstructsExecutablePreset()
    {
        var section = new ZapretSection
        {
            Name = "discord.com",
            HostListPaths = ["lists/discord.txt"],
            InlineDomains = ["discord.com"],
            IpSetPaths = [],
            DesyncRecipes = ["old_step"],
            IsPassThrough = false,
            RawArguments = ["--name=discord.com", "--filter-tcp=80,443", "--lua-desync=old_step"]
        };

        var template = new ZapretPreset
        {
            Name = "V12",
            FilePath = "v12.txt",
            GlobalArguments = ["--ctrack-disable=0"],
            Sections = [section]
        };

        var ind = new PresetIndividual
        {
            GenerationNumber = 5,
            Genes = new Dictionary<string, PresetGene>
            {
                ["discord.com"] = new PresetGene
                {
                    TcpPorts = "80,443-65535",
                    OutRange = "-d10",
                    DesyncSteps = ["multisplit:pos=1:seqovl=568:seqovl_pattern=tls_4pda"]
                }
            }
        };

        var resultPreset = ind.ToPreset(template);

        Assert.Contains("gen-5", resultPreset.Name);
        var reconstructedSection = resultPreset.Sections.First(s => s.Name == "discord.com");
        Assert.Contains("--out-range=-d10", reconstructedSection.RawArguments);
        Assert.Contains("--filter-tcp=80,443-65535", reconstructedSection.RawArguments);
        Assert.Equal("multisplit:pos=1:seqovl=568:seqovl_pattern=tls_4pda", reconstructedSection.DesyncRecipes[0]);
    }

    [Fact]
    public async Task EvolutionStore_SavesAndLoadsPopulation()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"pop_test_{Guid.NewGuid():N}.json");
        try
        {
            var store = new Zephyron.Supervisor.Evolution.EvolutionStore();
            var ind = new PresetIndividual
            {
                GenerationNumber = 3,
                FitnessScore = 0.88,
                ProbesPassed = 5,
                ProbesTotal = 6,
                AvgLatencyMs = 240,
                Genes = new Dictionary<string, PresetGene>
                {
                    ["test"] = new PresetGene { OutRange = "-d8", DesyncSteps = ["fake:blob=tls_google"] }
                }
            };

            await store.SaveAsync([ind], generation: 3, basePresetName: "TestBase", path: tempFile);

            var loaded = await store.LoadAsync(tempFile);
            Assert.NotNull(loaded);
            Assert.Equal(3, loaded.Generation);
            Assert.Equal("TestBase", loaded.BasePreset);
            Assert.Single(loaded.Population);
            Assert.Equal(0.88, loaded.Population[0].FitnessScore);
            Assert.True(loaded.Population[0].Genes.ContainsKey("test"));
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void PresetExporter_ExportsValidTxtFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"export_test_{Guid.NewGuid():N}");
        try
        {
            var exporter = new Zephyron.Supervisor.Evolution.PresetExporter();
            var template = new ZapretPreset
            {
                Name = "BaseV1",
                FilePath = "BaseV1.txt",
                GlobalArguments = ["--ctrack-disable=0", "--lua-init=@lua/test.lua"],
                Sections =
                [
                    new ZapretSection
                    {
                        Name = "mysection",
                        HostListPaths = ["lists/my.txt"],
                        InlineDomains = [],
                        IpSetPaths = [],
                        DesyncRecipes = ["old"],
                        IsPassThrough = false,
                        RawArguments = ["--name=mysection", "--filter-tcp=80,443"]
                    }
                ]
            };

            var winner = new PresetIndividual
            {
                GenerationNumber = 7,
                FitnessScore = 0.94,
                ProbesPassed = 6,
                ProbesTotal = 6,
                AvgLatencyMs = 120,
                Genes = new Dictionary<string, PresetGene>
                {
                    ["mysection"] = new PresetGene
                    {
                        TcpPorts = "80,443-65535",
                        OutRange = "-d6",
                        DesyncSteps = ["multisplit:pos=1:seqovl=568"]
                    }
                }
            };

            var exportedPath = exporter.Export(winner, template, tempDir);

            Assert.True(File.Exists(exportedPath));
            var content = File.ReadAllText(exportedPath);
            Assert.Contains("# Preset: Evolved BaseV1 (Gen 7)", content);
            Assert.Contains("# BuiltinVersion: evolved-gen-7", content);
            Assert.Contains("--name=mysection", content);
            Assert.Contains("--filter-tcp=80,443-65535", content);
            Assert.Contains("--out-range=-d6", content);
            Assert.Contains("--lua-desync=multisplit:pos=1:seqovl=568", content);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void MemeticLocalSearch_GeneratesValidVariations()
    {
        var rng = new Random(100);
        var pool = new GenePool
        {
            OutRanges = ["-d6", "-d8", "-d10"],
            SmartSplitPositions = ["1", "midsld"],
            SmartSeqOverlaps = [1, 568],
            KnownRecipeSteps = [["multisplit:pos=1:seqovl=568"]]
        };

        var ind = new PresetIndividual
        {
            GenerationNumber = 2,
            Genes = new Dictionary<string, PresetGene>
            {
                ["youtube"] = new PresetGene
                {
                    OutRange = "-d8",
                    DesyncSteps = ["fake:blob=tls_google:repeats=2", "multisplit:pos=1:seqovl=568"]
                }
            }
        };

        var variations = GeneticOperators.GenerateLocalVariations(ind, pool, rng, count: 4);

        Assert.Equal(4, variations.Count);
        foreach (var v in variations)
        {
            Assert.True(v.Genes.ContainsKey("youtube"));
            var g = v.Genes["youtube"];
            Assert.Contains(g.OutRange, pool.OutRanges);
            Assert.NotEmpty(g.DesyncSteps);
        }
    }

    [Fact]
    public void RebuildSectionArguments_PreservesCustomFilters()
    {
        var original = new ZapretSection
        {
            Name = "discord_custom",
            HostListPaths = ["lists/discord.txt"],
            InlineDomains = [],
            IpSetPaths = [],
            DesyncRecipes = ["old_recipe"],
            IsPassThrough = false,
            RawArguments =
            [
                "--name=discord_custom",
                "--filter-tcp=80,443",
                "--wf-raw-part=@windivert.filter/windivert_part.discord_media.txt",
                "--out-range=-d8",
                "--lua-desync=old_recipe"
            ]
        };

        var gene = new PresetGene
        {
            TcpPorts = "80,443-65535",
            OutRange = "-d10",
            DesyncSteps = ["fake:blob=tls_max:repeats=3", "multidisorder:pos=1,host+2:seqovl=1"]
        };

        var rebuilt = PresetIndividual.RebuildSectionArguments(original, gene);

        // Проверяем, что кастомный фильтр не потерялся
        Assert.Contains("--wf-raw-part=@windivert.filter/windivert_part.discord_media.txt", rebuilt);
        // Проверяем, что обновленные параметры подставились
        Assert.Contains("--filter-tcp=80,443-65535", rebuilt);
        Assert.Contains("--out-range=-d10", rebuilt);
        // Проверяем, что старый desync заменен новыми
        Assert.DoesNotContain("--lua-desync=old_recipe", rebuilt);
        Assert.Contains("--lua-desync=fake:blob=tls_max:repeats=3", rebuilt);
        Assert.Contains("--lua-desync=multidisorder:pos=1,host+2:seqovl=1", rebuilt);
    }
}
