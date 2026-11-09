using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Zephyron.Proxy;

namespace Zephyron.Gui.Views;

/// <summary>Джиттер и «Под нагрузкой» в основном замере, график истории с переключателем метрики.</summary>
public partial class SpeedView
{
    /// <summary>Сколько замеров на графике истории.</summary>
    private const int TrendShown = 20;

    /// <summary>Что рисует график: 0 загрузка, 1 отправка, 2 отклик, 3 под нагрузкой.</summary>
    private static int _trend;

    /// <summary>Показания отклика под нагрузкой идущего замера.</summary>
    private static List<double> _loaded = [];

    /// <summary>Что сейчас в ячейке «Под нагрузкой» идущего замера.</summary>
    private static string _loadedNow = "—";

    /// <summary>Разница соседних откликов — те же числа, что дают джиттер, по порядку.</summary>
    internal static List<double> Diffs(IReadOnlyList<double> pings) =>
        pings.Zip(pings.Skip(1), (a, b) => Math.Abs(a - b)).ToList();

    /// <summary>Ячейки джиттера и «под нагрузкой» — по итогу замера; <c>null</c> — пусто.</summary>
    private void ShowExtra(Shot? shot)
    {
        var entry = shot?.Entry;

        JitText.Text = entry?.JitterMs is { } jitter ? $"{jitter:0.#}" : "—";
        JitLine.Show(Diffs(shot?.Ping ?? []));

        LoadText.Text = entry?.LoadedPingMs is { } loaded ? $"{loaded:0}" : "—";
        LoadLine.Show(shot?.Loaded ?? []);

        if (entry?.Grade() is { } grade)
        {
            LoadLetter.Text = grade.ToString();
            LoadLetter.SetResourceReference(TextBlock.ForegroundProperty, grade is 'A' or 'B' ? "Success" : grade == 'C' ? "Warn" : "Danger");
            LoadGrade.Visibility = Visibility.Visible;
            LoadWord.Text = $"+{entry.LoadedIncrease():0} мс к простою";
            System.Windows.Automation.AutomationProperties.SetName(
                LoadGrade, $"Класс {grade}, отклик под нагрузкой растёт на {entry.LoadedIncrease():0} миллисекунд");
        }
        else
        {
            LoadGrade.Visibility = Visibility.Collapsed;
            LoadWord.Text = " ";
        }
    }

    /// <summary>Идущий замер: джиттер и отклик под нагрузкой на ходу.</summary>
    private void ShowLiveExtra()
    {
        JitText.Text = _ping.Count > 1 ? $"{Diffs(_ping).Average():0.#}" : "—";
        JitLine.Show(Diffs(_ping));
        LoadText.Text = _loadedNow;
        LoadLine.Show(_loaded);
        LoadGrade.Visibility = Visibility.Collapsed;
        LoadWord.Text = _loaded.Count > 0 ? "идёт замер" : " ";
    }

    private void OnTrend(object sender, RoutedEventArgs e)
    {
        _trend = int.Parse((string)((Button)sender).Tag);
        ShowTrend(SpeedHistory.Load());
    }

    /// <summary>
    /// График выбранной метрики по последним замерам выбранного пути, старые слева.
    /// </summary>
    /// <remarks>Путь один: скорость туннеля и прямая на одной кривой читались бы как скачки.</remarks>
    private void ShowTrend(IReadOnlyList<SpeedEntry> history)
    {
        var chips = new[] { TrendDown, TrendUp, TrendPing, TrendLoad };

        for (int i = 0; i < chips.Length; i++)
            Selector.SetIsSelected(chips[i], i == _trend);

        static double? Pick(SpeedEntry e, int metric) => metric switch
        {
            0 => e.DownMbps,
            1 => e.UpMbps,
            2 => e.PingMs,
            _ => e.LoadedIncrease(),
        };

        var values = history
            .Where(e => e.Tunnel == _tunnel)
            .Select(e => Pick(e, _trend))
            .OfType<double>()
            .Take(TrendShown)
            .Reverse()
            .ToList();

        TrendLine.Headroom = _trend >= 2 ? 1.5 : 1;
        TrendLine.Show(values);

        string unit = _trend < 2 ? "Мбит/с" : "мс";
        string what = _trend == 3 ? "рост отклика под нагрузкой" : "последние замеры";

        TrendCaption.Text = values.Count < 2
            ? "Меньше двух замеров этим путём — кривой пока нет"
            : $"{what}: {values.Count}, макс. {(_trend < 2 ? SpeedVerdict.Number(values.Max()) : $"{values.Max():0}")} {unit}"
              + (_tunnel ? ", через туннель" : ", напрямую");
    }
}
