using Zephyron.Proxy;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>
/// Повторы в hosts: что считается повтором и что можно убрать без последствий.
/// </summary>
/// <remarks>
/// Живой файл 23.09: имена Claude прибиты дважды — нашим блоком и блоком
/// ZapretGUI ниже.
/// </remarks>
public sealed class HostsDuplicatesTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"zephyron-hosts-dup-{Guid.NewGuid():N}");

    public void Dispose() => File.Delete(_path);

    private HostsDuplicateReport Find(string text)
    {
        File.WriteAllText(_path, text);
        return HostsDuplicates.Find(_path);
    }

    /// <summary>Повтор в чужом блоке с тем же адресом — убирается, блок назван.</summary>
    [Fact]
    public void ARepeatBelowOurBlockIsRemovable()
    {
        var report = Find("""
            # >>> zephyron begin >>>
            87.228.47.204 claude.ai
            87.228.47.204 api.anthropic.com
            # <<< zephyron end <<<
            # >>> zapretgui:hosts managed begin >>>
            87.228.47.204 claude.ai
            87.228.47.204 downloads.claude.ai
            # <<< zapretgui:hosts managed end <<<
            """);

        var repeat = Assert.Single(report.Repeats);

        Assert.Equal("claude.ai", repeat.Name);
        Assert.False(repeat.Conflicts);
        Assert.Equal(1, repeat.FirstLine);

        // Строка с claude.ai — повтор целиком; downloads.claude.ai — впервые.
        Assert.Equal([5], report.Removable);
        Assert.Equal(["Zapret GUI"], report.ForeignBlocks);
    }

    /// <summary>
    /// Другой адрес того же имени в том же месте — запасной адрес, не повтор:
    /// Windows отдаёт программам оба (замер 22.11).
    /// </summary>
    [Fact]
    public void ASecondAddressInTheSamePlaceIsNotARepeat()
    {
        var report = Find("""
            # >>> zephyron begin >>>
            5.175.161.3 claude.ai
            5.175.161.40 claude.ai
            # <<< zephyron end <<<
            1.2.3.4 example.com
            5.6.7.8 example.com
            """);

        Assert.False(report.Any);
        Assert.Empty(report.Removable);
    }

    /// <summary>
    /// Другой адрес из другого блока — спор: имя ведут два хозяина, и убирать
    /// чужую строку молча нельзя.
    /// </summary>
    [Fact]
    public void ADifferentAddressFromAnotherBlockIsAConflictAndStays()
    {
        var report = Find("""
            # >>> zephyron begin >>>
            87.228.47.204 claude.ai
            # <<< zephyron end <<<
            # >>> zapretgui:hosts managed begin >>>
            5.6.7.8 claude.ai
            # <<< zapretgui:hosts managed end <<<
            """);

        Assert.True(Assert.Single(report.Repeats).Conflicts);
        Assert.Equal(1, report.Conflicting);
        Assert.Empty(report.Removable);
    }

    /// <summary>Строка, где среди повторов есть новое имя, не убирается.</summary>
    [Fact]
    public void ALineWithANewNameStays()
    {
        var report = Find("""
            1.2.3.4 a.example
            1.2.3.4 a.example b.example
            """);

        Assert.Single(report.Repeats);
        Assert.Empty(report.Removable);
    }

    /// <summary>Выключенная запись повтором не считается: Windows её не читает.</summary>
    [Fact]
    public void ADisabledLineIsNotARepeat()
    {
        var report = Find("""
            1.2.3.4 example.com
            # 1.2.3.4 example.com
            """);

        Assert.False(report.Any);
    }

    /// <summary>Повтор внутри нашего блока строкой не убирается — блок ведём мы.</summary>
    [Fact]
    public void ARepeatInsideOurBlockIsNotRemovedByLine()
    {
        var report = Find("""
            1.2.3.4 example.com
            # >>> zephyron begin >>>
            1.2.3.4 example.com
            # <<< zephyron end <<<
            """);

        Assert.Single(report.Repeats);
        Assert.Empty(report.Removable);
    }

    [Fact]
    public void NamesCompareWithoutCase()
    {
        var report = Find("""
            1.2.3.4 Example.com
            1.2.3.4 example.COM
            """);

        Assert.Equal([1], report.Removable);
    }
}
