using System.IO;
using System.Windows;
using System.Windows.Controls;
using Zephyron.Core;
using Zephyron.Zapret;

namespace Zephyron.Gui.Views;

/// <summary>Вариант в списке подделок места: «как в пресете» (<see cref="Blob"/> пуст) или образец.</summary>
public sealed record FakeOption(string Text, string? Blob)
{
    // Стиль ComboBox показывает выбранное через ToString, а не DisplayMemberPath.
    public override string ToString() => Text;
}

/// <summary>Строка окна: одно место подделки и выбранный образец.</summary>
public sealed class FakeRow
{
    public required FakeSlot Slot { get; init; }

    public required IReadOnlyList<FakeOption> Options { get; init; }

    public required FakeOption Selected { get; set; }

    public string Section => Slot.Section + (Slot.Slot > 0 ? $" · подделка {Slot.Slot + 1}" : string.Empty);

    public string Details => Slot.Details;
}

/// <summary>
/// Выбор подделок секций пресета (<see cref="FakeChoice"/>); ответ — <see cref="Result"/>.
/// </summary>
public partial class FakePickerWindow : Window
{
    private readonly List<FakeRow> _rows;
    private readonly string _preset;
    private readonly IReadOnlyList<FakeSwap> _others;

    /// <summary>Выбор по этому пресету; <c>null</c> — отказались.</summary>
    public IReadOnlyList<FakeSwap>? Result { get; private set; }

    /// <param name="swaps">Все сохранённые выборы; чужих пресетов окно не трогает.</param>
    public FakePickerWindow(ZapretPreset preset, string presetName, IReadOnlyList<FakeSwap> swaps, string? zapretRoot)
    {
        InitializeComponent();

        _preset = presetName;
        _others = swaps.Where(s => !string.Equals(s.Preset, presetName, StringComparison.OrdinalIgnoreCase)).ToList();

        var available = FakeChoice.Available(zapretRoot);
        var mine = swaps.Where(s => string.Equals(s.Preset, presetName, StringComparison.OrdinalIgnoreCase)).ToList();

        _rows = FakeChoice.Slots(preset).Select(slot =>
        {
            var keep = new FakeOption($"Как в пресете — {slot.Blob}", null);
            var options = new List<FakeOption> { keep };
            options.AddRange(FakeChoice.Fitting(slot, available).Select(b => new FakeOption(b.Title, b.Id)));

            var chosen = mine.FirstOrDefault(s => s.Slot == slot.Slot && s.Section == slot.Section)?.Blob;

            // Выбранного нет среди подходящих (файл убрали) — показываем как есть, чтобы не потерять молча.
            if (chosen is not null && options.All(o => o.Blob != chosen))
                options.Add(new FakeOption(Path.GetFileNameWithoutExtension(chosen) + " (нет в bin)", chosen));

            return new FakeRow { Slot = slot, Options = options, Selected = options.FirstOrDefault(o => o.Blob == chosen) ?? keep };
        }).ToList();

        Head.Text = $"Подделки секций · {preset.Name}";
        Show(string.Empty);
    }

    private void Show(string query)
    {
        var found = FakeChoice.Search(_rows.Select(r => r.Slot), query).ToHashSet();
        var shown = _rows.Where(r => found.Contains(r.Slot)).ToList();

        List.ItemsSource = shown;
        Count.Text = _rows.Count == 0
            ? "В секциях этого пресета нет подделок."
            : shown.Count == 0 ? "Ничего не найдено." : $"Мест подделок: {shown.Count} из {_rows.Count}.";
    }

    private void OnSearch(object sender, TextChangedEventArgs e) => Show(Search.Text);

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Result = [.. _others, .. _rows.Where(r => r.Selected.Blob is not null)
            .Select(r => new FakeSwap(_preset, r.Slot.Section, r.Slot.Slot, r.Selected.Blob!))];
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
