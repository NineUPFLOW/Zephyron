using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using Xunit;

namespace Zephyron.Gui.Tests;

/// <summary>
/// Каждая кисть и каждый стиль, на который ссылается разметка, существует.
/// </summary>
/// <remarks>
/// <para>
/// Дополняет <see cref="ViewsLoadTests"/> там, где тот бессилен. Созданием
/// вкладки проверяется лишь то, что разбирается сразу; содержимое
/// <c>DataTemplate</c> разбирается в мгновение, когда в списке появится первый
/// элемент, — то есть уже у человека и уже с данными. Опечатка в имени стиля
/// внутри шаблона строки проходит и сборку, и создание вкладки, и падает
/// ровно тогда, когда проверка что-то нашла.
/// </para>
/// <para>
/// Поэтому здесь читается текст разметки, а не собранное дерево: ссылки
/// берутся из файлов, а существующие имена — из живых словарей темы. Второе
/// важно: сверять текст с текстом значило бы проверять, что мы дважды
/// написали одно и то же, а не что оно работает.
/// </para>
/// </remarks>
public sealed class ResourceKeyTests
{
    /// <summary>Ссылки вида <c>{StaticResource Имя}</c> и <c>{DynamicResource Имя}</c>.</summary>
    /// <remarks>
    /// Только простые имена. Ссылки на системные ключи записываются через
    /// <c>{x:Static}</c> и под это не подходят — и правильно: их существование
    /// обеспечивает не наша тема.
    /// </remarks>
    private static readonly Regex Reference = new(
        @"\{(?:Static|Dynamic)Resource\s+([A-Za-z_][A-Za-z0-9_]*)\s*\}",
        RegexOptions.Compiled);

    private static readonly Regex Declaration = new(
        @"x:Key=""([A-Za-z_][A-Za-z0-9_]*)""",
        RegexOptions.Compiled);

    private static string Folder => Path.Combine(AppContext.BaseDirectory, "Xaml");

    /// <summary>Имена, объявленные темами.</summary>
    /// <remarks>
    /// Берутся из словарей, поднятых по-настоящему: так учитывается и то,
    /// что одна тема достраивает другую.
    /// </remarks>
    private static HashSet<string> Defined()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        Sta.Run(() =>
        {
            foreach (var theme in new[] { "Tokens", "Palette", "Controls", "Light" })
            {
                var dictionary = new ResourceDictionary
                {
                    Source = new Uri(
                        $"pack://application:,,,/Zephyron;component/Theme/{theme}.xaml",
                        UriKind.Absolute),
                };

                foreach (var key in dictionary.Keys)
                {
                    if (key is string name)
                        names.Add(name);
                }
            }
        });

        return names;
    }

    [Fact]
    public void The_xaml_is_where_the_test_expects_it()
    {
        // Отдельной проверкой: пустая папка сделала бы все остальные
        // проверки этого файла зелёными, ничего не проверив.
        Assert.True(Directory.Exists(Folder), Folder);
        Assert.True(Directory.GetFiles(Folder, "*.xaml").Length >= 12);
    }

    [Fact]
    public void Every_referenced_style_and_brush_exists()
    {
        var themed = Defined();
        var missing = new List<string>();

        foreach (var file in Directory.GetFiles(Folder, "*.xaml"))
        {
            var text = File.ReadAllText(file);

            // Имена, объявленные в самом файле: шаблон строки, локальная
            // кисть, свой стиль кнопки. Они не в теме и быть там не должны.
            var own = Declaration.Matches(text)
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);

            foreach (Match match in Reference.Matches(text))
            {
                var name = match.Groups[1].Value;

                if (!themed.Contains(name) && !own.Contains(name))
                    missing.Add($"{Path.GetFileName(file)}: {name}");
            }
        }

        Assert.Empty(missing);
    }

    /// <summary>Ссылка вида <c>{StaticResource Имя}</c> — только она требует порядка.</summary>
    /// <remarks>
    /// <c>DynamicResource</c> ищется на бегу по всему дереву и на объявление
    /// ниже себя не жалуется, а <c>StaticResource</c> берёт значение на разбор
    /// и видит лишь то, что объявлено выше. Ссылка вперёд разбирается молча
    /// у нас на глазах и падает у человека: шапка разделов дала именно это —
    /// <c>«Не удается найти ресурс с именем "Title"»</c> при показе окна.
    /// </remarks>
    private static readonly Regex StaticReference = new(
        @"\{StaticResource\s+([A-Za-z_][A-Za-z0-9_]*)\s*\}",
        RegexOptions.Compiled);

    /// <summary>Порядок, в котором словари тем ложатся друг на друга.</summary>
    /// <remarks>
    /// Тот же, что в App.xaml и в Sta.Prepare: токены, палитра, стили. Ссылка
    /// на имя из более раннего словаря законна; из более позднего — нет.
    /// </remarks>
    private static readonly string[] MergeOrder = ["Tokens.xaml", "Palette.xaml", "Controls.xaml"];

    [Fact]
    public void Every_static_reference_is_declared_before_its_use()
    {
        var declaredBefore = new HashSet<string>(StringComparer.Ordinal);
        var forward = new List<string>();

        foreach (var name in MergeOrder)
        {
            var file = Path.Combine(Folder, name);

            if (!File.Exists(file))
                continue;

            var text = File.ReadAllText(file);
            var own = Declaration.Matches(text)
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);

            // Номера строк объявлений: имя видно лишь ниже первого из них.
            var at = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (Match match in Declaration.Matches(text))
            {
                var key = match.Groups[1].Value;

                if (!at.ContainsKey(key))
                    at[key] = LineOf(text, match.Index);
            }

            foreach (Match match in StaticReference.Matches(text))
            {
                var key = match.Groups[1].Value;

                // Чужое имя — забота Every_referenced_style_and_brush_exists.
                if (!own.Contains(key) && !declaredBefore.Contains(key))
                    continue;

                if (own.Contains(key) && LineOf(text, match.Index) < at[key])
                    forward.Add($"{name}: {key} (объявлен на строке {at[key]}, а взят раньше)");
            }

            declaredBefore.UnionWith(own);
        }

        Assert.Empty(forward);
    }

    private static int LineOf(string text, int index)
    {
        var line = 1;

        for (int i = 0; i < index; i++)
            if (text[i] == '\n')
                line++;

        return line;
    }
}
