using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace Zephyron.Gui.Tests;

/// <summary>
/// «Сигнал»: общие стили и палитры держат договорённости спецификации.
/// </summary>
public sealed class SignalStylesTests
{
    private static ResourceDictionary Load(string name) => new()
    {
        Source = new Uri($"pack://application:,,,/Zephyron;component/Theme/{name}.xaml", UriKind.Absolute),
    };

    /// <summary>Запасная светлая палитра несёт те же ключи, что тёмная, — стили не знают, какая из них стоит.</summary>
    [Fact]
    public void LightPaletteHasEveryKeyOfTheDarkOne()
    {
        Sta.Run(() =>
        {
            var dark = Load("Palette");
            var light = Load("Light");

            foreach (var key in dark.Keys.Cast<object>())
                Assert.True(light.Contains(key), $"в Light.xaml нет ключа {key}");

            Assert.True(light.Contains("FieldLine") && light.Contains("Scrim"));
        });
    }

    /// <summary>Новые стили из спецификации существуют и применяются к своим элементам.</summary>
    [Fact]
    public void NewImplicitAndStateStylesResolve()
    {
        Sta.Run(() =>
        {
            foreach (var key in new[] { "Metric", "MetricUnit", "StateWord", "EmptyState", "BusyState", "ErrorState", "StateMark" })
                Assert.IsType<Style>(Application.Current.FindResource(key));

            foreach (var type in new[] { typeof(CheckBox), typeof(RadioButton), typeof(ListBox), typeof(ListBoxItem), typeof(Slider), typeof(ProgressBar) })
                Assert.IsType<Style>(Application.Current.FindResource(type));
        });
    }

    /// <summary>Стили с шаблонами собираются и измеряются без падения (шаблон — разметка, ошибки видны только на показе).</summary>
    [Fact]
    public void TemplatedControlsMeasure()
    {
        Sta.Run(() =>
        {
            var panel = new StackPanel();

            panel.Children.Add(new CheckBox { Content = "Флажок", IsChecked = null });
            panel.Children.Add(new RadioButton { Content = "Выбор", IsChecked = true });
            panel.Children.Add(new ListBox { ItemsSource = new[] { "а", "б" }, SelectedIndex = 0 });
            panel.Children.Add(new Slider { Minimum = 0, Maximum = 10, Value = 4, Width = 160 });
            panel.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 160 });
            panel.Children.Add(new ProgressBar { Value = 40, Width = 160 });
            panel.Children.Add(new TextBox { Text = "x" });
            panel.Children.Add(new ContentControl { Style = (Style)Application.Current.FindResource("EmptyState") });
            panel.Children.Add(new ContentControl { Style = (Style)Application.Current.FindResource("BusyState") });
            panel.Children.Add(new ContentControl { Style = (Style)Application.Current.FindResource("ErrorState") });

            foreach (var tag in new[] { "Off", "Starting", "Running", "Degraded", "Failed" })
                panel.Children.Add(new Control { Style = (Style)Application.Current.FindResource("StateMark"), Tag = tag });

            panel.Measure(new Size(400, 2000));
            panel.Arrange(new Rect(panel.DesiredSize));

            Assert.True(panel.DesiredSize.Height > 100);
        });
    }

    /// <summary>
    /// Цифры метрики не дрожат: «1111» и «0000» одной ширины. Не сошлись —
    /// шрифт не отдаёт tnum, и стиль Metric надо перевести на MonoFont.
    /// </summary>
    [Fact]
    public void MetricDigitsAreTabular()
    {
        Sta.Run(() =>
        {
            double Width(string text)
            {
                var block = new TextBlock { Text = text, Style = (Style)Application.Current.FindResource("Metric") };
                block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                return block.DesiredSize.Width;
            }

            Assert.Equal(Width("0000"), Width("1111"), 1);
        });
    }
}
