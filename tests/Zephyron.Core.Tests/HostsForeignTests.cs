using Zephyron.Proxy;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>Чужие строки hosts: перекрывающие маршрут и перепутанные имя с адресом.</summary>
public sealed class HostsForeignTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"zephyron-hosts-{Guid.NewGuid():N}.txt");

    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_file)!, Path.GetFileName(_file) + "*"))
        {
            try { File.Delete(f); } catch (Exception) { }
        }
    }

    private static string? Route(string name) => name == "routed.example" ? "#1 domain:routed.example -> proxy" : null;

    private void Write() => File.WriteAllText(_file, string.Join("\r\n",
        "127.0.0.1 localhost",
        HostsEditor.BlockBegin,
        "1.1.1.1 routed.example",
        HostsEditor.BlockEnd,
        "9.9.9.9 routed.example # чужая",
        "dt-live.passtechgames.com 162.19.84.58",
        "# 8.8.8.8 routed.example",
        "5.5.5.5 quiet.example"));

    [Fact]
    public void Finds_routed_pins_and_swapped_lines_outside_our_block()
    {
        Write();
        var found = HostsEditor.FindForeign(_file, Route);

        Assert.Equal([5, 6], found.Select(f => f.LineNumber));
        Assert.Contains("перекрывает маршрут", found[0].Reason);
        Assert.Contains("перепутаны имя и адрес", found[1].Reason);
    }

    [Theory]
    [InlineData("#0 hostlist:config/lists/telegram.txt -> proxy", "telegram (через VPN)")]
    [InlineData("#3 domain:*.example.org -> desync", "example.org (десинк)")]
    [InlineData("что-то своё", "«что-то своё»")]
    public void Route_is_said_in_words(string route, string said) => Assert.Equal(said, HostsEditor.Readable(route));

    [Fact]
    public void Remove_drops_exactly_those_lines_with_backup()
    {
        Write();
        var found = HostsEditor.FindForeign(_file, Route);
        // Чужая для файла строка (сдвиг или правка) не трогается.
        var stale = new ForeignHostsLine(1, "не та строка", "—");

        Assert.Equal(2, HostsEditor.RemoveForeign([.. found, stale, new ForeignHostsLine(999, "x", "—")], _file));

        var after = File.ReadAllLines(_file);

        Assert.Equal(6, after.Length);
        Assert.DoesNotContain(after, l => l.StartsWith("9.9.9.9") || l.StartsWith("dt-live"));
        Assert.Contains(after, l => l.StartsWith("1.1.1.1"));
        Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(_file)!, Path.GetFileName(_file) + ".zephyron-*.bak"));
    }
}
