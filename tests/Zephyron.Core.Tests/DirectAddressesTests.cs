using Zephyron.Core.Rules;
using Zephyron.Zapret;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>«Напрямую» по адресам — мимо всех секций winws2; подделка SYN — только на портах HTTPS.</summary>
public sealed class DirectAddressesTests
{
    private static UserRuleEntry Rule(MatchKind match, string value, RoutingMode mode, bool off = false) =>
        new() { Match = match, Value = value, Mode = mode, Off = off };

    [Fact]
    public void OnlyDirectAddressRulesAreTaken()
    {
        var networks = DirectAddresses.Choose(
        [
            Rule(MatchKind.Ip, "203.0.113.0/24", RoutingMode.Direct),
            Rule(MatchKind.Ip, "198.51.100.0/24", RoutingMode.Proxy),
            Rule(MatchKind.Ip, "192.0.2.0/24", RoutingMode.Direct, off: true),
            Rule(MatchKind.Domain, "game.example", RoutingMode.Direct),
        ], null);

        Assert.Equal(["203.0.113.0/24"], networks);
    }

    [Fact]
    public void FileNeverEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), "direct-" + Guid.NewGuid().ToString("N") + ".txt");

        try
        {
            DirectAddresses.Write([], path);
            Assert.Equal([DirectAddresses.Placeholder], File.ReadAllLines(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EverySectionSkipsDirectNetworks()
    {
        var preset = new PresetReader().Load(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "presets", "Universal V12 Lite.txt"));
        var arguments = WinwsCommandLine.Build(preset, directList: "C:/z/runtime/desync-direct.txt").ToList();

        Assert.Equal(preset.Sections.Count(s => !WinwsCommandLine.FiltersByName(s)), arguments.Count(a => a == "--ipset-exclude=C:/z/runtime/desync-direct.txt"));
    }

    [Fact]
    public void SynDataStaysOnHttpsPorts()
    {
        string[] fallback = ["--filter-tcp=80,443-65535", "--ipset=lists/ipset-discord.txt", "--lua-desync=send:repeats=3", "--lua-desync=syndata:blob=tls_google"];
        string[] telegram = ["--filter-tcp=80,443,5222", "--ipset=lists/ipset-telegram.txt", "--lua-desync=syndata:blob=tls_google"];

        Assert.Contains("--filter-tcp=" + VideoWindow.SynDataPorts, VideoWindow.SparedFromSynData(fallback));
        Assert.Contains("--filter-tcp=80,443,5222", VideoWindow.SparedFromSynData(telegram));
    }
}

/// <summary>Строка winws2 со всеми добавками помещается в предел Windows на любом пресете.</summary>
public sealed class CommandLineLengthTests
{
    [Fact]
    public void EveryPresetFitsWithAllAdditions()
    {
        var presets = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "presets"), "*.txt");
        var longPath = "C:/Users/Someone/Desktop/zapret/Zephyron/runtime/desync-direct.txt";

        foreach (var file in presets)
        {
            var arguments = WinwsCommandLine.Build(new PresetReader().Load(file), videoWindow: true,
                panelHosts: ["panel.example.com", "sub.example.org"], directList: longPath);

            Assert.True(WinwsCommandLine.Length(arguments) < WinwsCommandLine.SafeLength, Path.GetFileName(file));
        }
    }
}
