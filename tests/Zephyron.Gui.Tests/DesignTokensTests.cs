using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using Xunit;

namespace Zephyron.Gui.Tests;

/// <summary>
/// Токены оформления совпадают с документом системы дизайна.
/// </summary>
/// <remarks>
/// <para>
/// Токены — договор между разметкой и документом: имя одно, значение одно.
/// Разойдись они, и в документе было бы одно оформление, а в окне другое,
/// и по виду не понять, какое верно. Сверяются они здесь, а не глазами.
/// </para>
/// <para>
/// Читается текст обеих сторон: значения — из живого словаря
/// <c>Tokens.xaml</c>, имена и числа — из таблицы <c>docs/design-system.md</c>.
/// Так проверка ловит и забытый в документе токен, и токен с чужим числом.
/// </para>
/// </remarks>
public sealed class DesignTokensTests
{
    /// <summary>Строка таблицы документа: <c>| `RLg` | 16 | описание |</c>.</summary>
    private static readonly Regex Row = new(
        @"^\|\s*`([A-Za-z_][A-Za-z0-9_]*)`\s*\|\s*([0-9.,]+)\s*\|",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static string? Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Zephyron.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }

    [Fact]
    public void Every_documented_token_has_the_documented_value()
    {
        if (Repository() is not { } repo)
            return;

        var document = Path.Combine(repo, "docs", "design-system.md");

        Assert.True(File.Exists(document), document);

        var rows = Row.Matches(File.ReadAllText(document));

        // Иначе таблицу однажды перепишут так, что разбор её не увидит,
        // и проверка станет зелёной, ничего не проверив.
        Assert.True(rows.Count >= 14, $"в документе нашлось всего {rows.Count} токенов");

        Sta.Run(() =>
        {
            var tokens = new ResourceDictionary
            {
                Source = new Uri(
                    "pack://application:,,,/Zephyron;component/Theme/Tokens.xaml",
                    UriKind.Absolute),
            };

            foreach (Match row in rows)
            {
                var name = row.Groups[1].Value;
                var wanted = row.Groups[2].Value.Replace(',', '.');

                Assert.True(tokens.Contains(name), $"в Tokens.xaml нет токена {name}");

                var actual = tokens[name] switch
                {
                    double number => number.ToString("0.###"),
                    Thickness thickness => $"{thickness.Left},{thickness.Top},{thickness.Right},{thickness.Bottom}",
                    var other => other?.ToString() ?? "",
                };

                // Толщины пишутся через запятую, числа — через точку; сверяем
                // в одном виде, чтобы «32,28,32,28» и «32.28.32.28» сошлись.
                Assert.Equal(wanted, actual.Replace(',', '.'));
            }
        });
    }
}
