using System.IO;
using System.Windows.Media;
using Xunit;
using Zephyron.Core.Themes;

namespace Zephyron.Gui.Tests;

/// <summary>Accent2 читается (4.5:1) на подложке каждой встроенной темы и не сливается с Success.</summary>
public class FixesThemeTests
{
    private static string ThemesRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Zephyron.sln")) && Directory.Exists(Path.Combine(directory.FullName, "themes")))
                return Path.Combine(directory.FullName, "themes");

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("themes");
    }

    private static ThemeColor From(Color c) => new(255, c.R, c.G, c.B);

    [Fact]
    public void Accent2_reads_on_backdrop_and_stays_apart_from_Success_in_every_theme() => Sta.Run(() =>
    {
        var root = ThemesRoot();
        var ids = Directory.GetDirectories(root)
            .Where(d => File.Exists(Path.Combine(d, "theme.json")))
            .Select(d => Path.GetFileName(d)!)
            .ToList();
        Assert.True(ids.Count >= 14, "найдено тем: " + ids.Count);
        var bad = new List<string>();

        foreach (var id in ids)
        {
            var load = ThemeLoader.Load(id, root);

            if (load.Theme is null)
            {
                bad.Add($"{id}: не загрузилась");
                continue;
            }

            var built = Zephyron.Gui.Themes.Build(load.Theme, []);
            var a2 = From((Color)built["Accent2Color"]);
            var ok = From((Color)built["SuccessColor"]);
            double ratio = ThemeContrast.Ratio(a2.Luminance, load.Theme[ThemeSlots.Backdrop].Luminance);

            if (ratio < 4.5)
                bad.Add($"{id}: Accent2 {a2} к подложке {ratio:F2}:1 < 4.5");

            var (h2, _, _) = ColorMath.ToHsl(a2);
            var (hs, _, _) = ColorMath.ToHsl(ok);
            double gap = Math.Abs(h2 - hs) % 360;
            gap = Math.Min(gap, 360 - gap);

            if (gap < 30)
                bad.Add($"{id}: тон Accent2 {h2:F0} и Success {hs:F0} — разница {gap:F0} < 30");
        }

        Assert.True(bad.Count == 0, string.Join(Environment.NewLine, bad));
    });
}
