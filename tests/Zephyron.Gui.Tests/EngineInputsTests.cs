using System.IO;
using Zephyron.Gui;
using Xunit;

namespace Zephyron.Gui.Tests;

/// <summary>Отпечаток настроек движков: окно и вид его не меняют, маршруты и DNS — меняют.</summary>
public sealed class EngineInputsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inputs-" + Guid.NewGuid().ToString("N"));

    public EngineInputsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Settings => Path.Combine(_dir, "zephyron.json");

    private string Rules => Path.Combine(_dir, "rules.user.yaml");

    [Fact]
    public void OnlyEngineInputsCount()
    {
        File.WriteAllText(Settings, "{\"DnsServer\":\"1.1.1.1\",\"Theme\":\"dark\"}");
        File.WriteAllText(Rules, "rules: []");
        var first = EngineInputs.Current(Settings, Rules);

        File.WriteAllText(Settings, "{\"DnsServer\":\"1.1.1.1\",\"Theme\":\"light\",\"PreferredServer\":\"x\"}");
        Assert.Equal(first, EngineInputs.Current(Settings, Rules));

        File.WriteAllText(Settings, "{\"DnsServer\":\"8.8.8.8\",\"Theme\":\"light\"}");
        Assert.NotEqual(first, EngineInputs.Current(Settings, Rules));

        File.WriteAllText(Settings, "{\"DnsServer\":\"1.1.1.1\"}");
        File.WriteAllText(Rules, "rules:\n  - match: domain\n    value: \"a.com\"\n    mode: proxy\n");
        Assert.NotEqual(first, EngineInputs.Current(Settings, Rules));
    }
}
