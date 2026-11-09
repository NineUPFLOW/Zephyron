using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Zephyron.Core.Programs;

namespace Zephyron.Gui;

/// <summary>
/// Значок программы по имени процесса — для «Наблюдения», где известно только имя файла.
/// </summary>
/// <remarks>
/// Путь берётся у запущенных процессов (RunningPrograms), сам значок — из файла
/// (<see cref="ProgramIcons"/>, замороженный). Всё в пуле потоков, не в потоке окна:
/// перебор процессов и чтение значка занимают десятки миллисекунд. Найденное помнится,
/// не найденное — полминуты: программа могла уже завершиться, а могла запуститься позже.
/// </remarks>
internal static class ProcessIcons
{
    private static readonly ConcurrentDictionary<string, Task<ImageSource?>> Found = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DateTime> Missed = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(30);

    private static readonly object Gate = new();
    private static Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _listedAt = DateTime.MinValue;

    /// <summary>Значок процесса; <c>null</c> — путь неизвестен или значок не достаётся.</summary>
    public static Task<ImageSource?> OfAsync(string process)
    {
        if (string.IsNullOrWhiteSpace(process) || process == "?")
            return Task.FromResult<ImageSource?>(null);

        if (Missed.TryGetValue(process, out var at) && DateTime.UtcNow - at < Retry)
            return Task.FromResult<ImageSource?>(null);

        return Look(process);
    }

    // Промах убираем из Found уже после того, как задача в нём точно лежит:
    // удаление изнутри задачи могло обогнать GetOrAdd, и пустой ответ застревал навсегда.
    private static async Task<ImageSource?> Look(string process)
    {
        var task = Found.GetOrAdd(process, name => Task.Run<ImageSource?>(() =>
            PathOf(name) is { } path ? ProgramIcons.Of(path) : null));

        var icon = await task;

        if (icon is null)
        {
            Missed[process] = DateTime.UtcNow;
            ((ICollection<KeyValuePair<string, Task<ImageSource?>>>)Found).Remove(new(process, task));
        }

        return icon;
    }

    private static string? PathOf(string process)
    {
        lock (Gate)
        {
            if (_paths.TryGetValue(process, out var known))
                return known;

            // Список процессов не чаще раза в 10 секунд: шумный сеанс спрашивает о сотне имён.
            if (DateTime.UtcNow - _listedAt < TimeSpan.FromSeconds(10))
                return null;

            try
            {
                _paths = RunningPrograms.List()
                    .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().Path, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                // Процессы не перечислились — значков нет, строки как были.
            }

            _listedAt = DateTime.UtcNow;

            return _paths.GetValueOrDefault(process);
        }
    }
}

/// <summary>Значок программы 16x16: свой, а пока не нашёлся или его нет — общий глиф.</summary>
internal sealed class ProcessIcon : ContentControl
{
    public static readonly DependencyProperty ProcessProperty = DependencyProperty.Register(
        nameof(Process), typeof(string), typeof(ProcessIcon),
        new PropertyMetadata(string.Empty, (d, _) => ((ProcessIcon)d).Reload()));

    public string Process
    {
        get => (string)GetValue(ProcessProperty);
        set => SetValue(ProcessProperty, value);
    }

    public ProcessIcon()
    {
        Width = 16;
        Height = 16;
        IsTabStop = false;
        Focusable = false;
        Content = Glyph();
    }

    private static TextBlock Glyph()
    {
        var glyph = new TextBlock { Text = "", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "Faint");

        return glyph;
    }

    private async void Reload()
    {
        // Строка таблицы переиспользуется (Recycling): значок прежней программы убираем сразу.
        var wanted = Process;
        Content = Glyph();

        ImageSource? icon;

        try
        {
            icon = await ProcessIcons.OfAsync(wanted);
        }
        catch (Exception)
        {
            return;
        }

        // Пока ждали, строке могли отдать другую программу.
        if (icon is not null && wanted == Process)
            Content = new Image { Source = icon, Width = 16, Height = 16, SnapsToDevicePixels = true };
    }
}
