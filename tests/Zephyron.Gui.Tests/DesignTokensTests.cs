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
                    // Толщина и скругление с равными сторонами записаны
                    // в документе одним числом: «1.5» и Thickness(1.5), «12»
                    // и CornerRadius(12) — одно и то же, а «1.5,1.5,1.5,1.5»
                    // в таблице только мешало бы читать.
                    Thickness t when t.Left == t.Top && t.Top == t.Right && t.Right == t.Bottom
                        => t.Left.ToString("0.###"),
                    CornerRadius r when r.TopLeft == r.TopRight && r.TopRight == r.BottomRight && r.BottomRight == r.BottomLeft
                        => r.TopLeft.ToString("0.###"),
                    Thickness thickness => $"{thickness.Left},{thickness.Top},{thickness.Right},{thickness.Bottom}",
                    CornerRadius radius => $"{radius.TopLeft},{radius.TopRight},{radius.BottomRight},{radius.BottomLeft}",
                    var other => other?.ToString() ?? "",
                };

                // Толщины пишутся через запятую, числа — через точку; сверяем
                // в одном виде, чтобы «32,28,32,28» и «32.28.32.28» сошлись.
                Assert.Equal(wanted, actual.Replace(',', '.'));
            }
        });
    }

    /// <summary>Токен оформления, ждущий структуру, объявлен структурой, а не числом.</summary>
    /// <remarks>
    /// <para>
    /// Свойства <c>BorderThickness</c>, <c>Margin</c>, <c>Padding</c>
    /// и <c>CornerRadius</c> принимают только свои структуры. Голое число для
    /// них — не тот вид, и WPF роняет разбор шаблона при первом показе:
    /// «"1,5" не является допустимым значением для свойства "BorderThickness"»
    /// (11.10, токен <c>Stroke</c>) и «"12" не является допустимым значением
    /// для свойства "CornerRadius"» (12.10, токен <c>RMd</c>).
    /// </para>
    /// <para>
    /// У толщин и скруглений причина ещё и в культуре: WPF переводит число
    /// в строку по текущей, и на русской Windows дробь выходит с запятой,
    /// которую разбор толщин не принимает. На английской локали дробь потому
    /// и не видна, что точка. Со структурой разбора нет вовсе — значит, ошибка
    /// не зависит ни от языка системы, ни от целости числа: число не годится
    /// ни дробное, ни целое.
    /// </para>
    /// <para>
    /// Здесь сверяется именно вид токена, а не одно падение: свойству, ждущему
    /// структуру, обязан достаться токен той же структуры. Иначе — ошибка, и
    /// падает сборка, а не окно у человека. Читается текст разметки, значит,
    /// ловится и на машине без WPF, где окно не поднять.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_geometrical_token_carries_no_number()
    {
        if (Repository() is not { } repo)
            return;

        // Свойства, которые WPF принимает только как структуру. Встречаются
        // двумя видами: прямо в разметке («BorderThickness="{StaticResource K}"»)
        // и в шаблоне через Setter («Property="BorderThickness" Value="{…}"»).
        // Оба падения были о шаблоне: он собирается уже в окне, поэтому и упало
        // у человека, а не на сборке.
        var values = new Regex(
            @"(?<name>BorderThickness|Margin|Padding|CornerRadius)\s*=\s*""\{(?:Static|Dynamic)Resource\s+(?<key>[A-Za-z_][A-Za-z0-9_]*)\}""",
            RegexOptions.Compiled);

        var setters = new Regex(
            @"<Setter\b[^>]*?Property\s*=\s*""(?<name>BorderThickness|Margin|Padding|CornerRadius)""[^>]*?"
            + @"Value\s*=\s*""\{(?:Static|Dynamic)Resource\s+(?<key>[A-Za-z_][A-Za-z0-9_]*)\}""",
            RegexOptions.Compiled);

        // Объявления в Tokens.xaml: структура или число. Первое типом (Thickness,
        // CornerRadius), второе разметкой sys:Double.
        var structures = new Regex(
            @"<(?<type>Thickness|CornerRadius)\s+x:Key=""(?<key>[A-Za-z_][A-Za-z0-9_]*)""",
            RegexOptions.Compiled);

        var numbers = new Regex(
            @"<sys:Double\s+x:Key=""(?<key>[A-Za-z_][A-Za-z0-9_]*)""",
            RegexOptions.Compiled);

        var tokens = File.ReadAllText(Path.Combine(repo, "gui", "Zephyron.Gui", "Theme", "Tokens.xaml"));

        var structure = structures.Matches(tokens)
            .Select(m => m.Groups["key"].Value)
            .ToHashSet(StringComparer.Ordinal);

        var number = numbers.Matches(tokens)
            .Select(m => m.Groups["key"].Value)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(repo, "gui"), "*.xaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);

            foreach (var use in values.Matches(text).Cast<Match>().Concat(setters.Matches(text).Cast<Match>()))
            {
                var key = use.Groups["key"].Value;

                if (structure.Contains(key))
                    continue;

                Assert.False(number.Contains(key),
                    $"{Path.GetFileName(file)}: токен {key} (число) применён к "
                    + $"{use.Groups["name"].Value}, а оно ждёт структуру — это уронит шаблон.");
            }
        }
    }
}
