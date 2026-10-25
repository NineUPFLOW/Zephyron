using Zephyron.Gui.Views;
using Xunit;

namespace Zephyron.Gui.Tests;

/// <summary>Плашка «Главной»: слово, знак и тинт по состоянию; метрики без замера — «—».</summary>
public sealed class HomeStateTests
{
    [Theory]
    [InlineData(HomeState.Off, "Выключен")]
    [InlineData(HomeState.Starting, "Запуск")]
    [InlineData(HomeState.Running, "Работает")]
    [InlineData(HomeState.Degraded, "Работает частично")]
    [InlineData(HomeState.Failed, "Сбой")]
    public void The_hero_says_the_state_in_words_and_shape(HomeState state, string word)
    {
        Sta.Run(() =>
        {
            var view = new StatusView();

            view.Present(state, "пояснение", HomeState.Off, HomeState.Off);

            Assert.Equal(word, view.StateLine.Text);
            Assert.Equal(state == HomeState.Off, view.Tint.Visibility == System.Windows.Visibility.Collapsed);

            // Приходящий знак — тот, что показан (второй, т. к. первый показ меняет их местами).
            Assert.Contains(state.ToString(), new[] { view.MarkA.Tag, view.MarkB.Tag });
        });
    }

    [Fact]
    public void A_metric_without_a_reading_is_a_dash_not_zero()
    {
        Sta.Run(() =>
        {
            var view = new StatusView();

            Assert.Equal("—", view.NetDown.Text);
            Assert.Equal("—", view.NetUp.Text);
            Assert.Equal("—", view.NetPing.Text);
            Assert.Equal("—", view.NetLoss.Text);
        });
    }

    /// <summary>Длинная строка в плитке не раздвигает колонку: края плиток соседних рядов совпадают.</summary>
    [Fact]
    public void A_long_line_does_not_widen_its_tile_column()
    {
        Sta.Run(() =>
        {
            static System.Windows.Controls.Grid Row(string right)
            {
                var row = new System.Windows.Controls.Grid { Width = 1000 };
                row.ColumnDefinitions.Add(new() { Width = new(1.75, System.Windows.GridUnitType.Star) });
                row.ColumnDefinitions.Add(new() { Width = new(12) });
                row.ColumnDefinitions.Add(new() { Width = new(1, System.Windows.GridUnitType.Star), MinWidth = 250 });
                var cell = new FitWidth { Child = new System.Windows.Controls.TextBlock { Text = right, TextTrimming = System.Windows.TextTrimming.CharacterEllipsis } };
                System.Windows.Controls.Grid.SetColumn(cell, 2);
                row.Children.Add(cell);
                row.Measure(new(1000, double.PositiveInfinity));
                row.Arrange(new(0, 0, 1000, row.DesiredSize.Height));
                return row;
            }

            var shortRow = Row("коротко");
            var longRow = Row(new string('ы', 400));

            Assert.Equal(shortRow.ColumnDefinitions[2].ActualWidth, longRow.ColumnDefinitions[2].ActualWidth, 1);
            Assert.Equal(988 / 2.75, longRow.ColumnDefinitions[2].ActualWidth, 1);

            // Плитка не считает себя шире колонки — иначе её правый край обрезается.
            var tile = ((FitWidth)longRow.Children[0]).Child;
            Assert.True(tile.DesiredSize.Width <= longRow.ColumnDefinitions[2].ActualWidth + 0.5,
                $"плитка {tile.DesiredSize.Width} шире колонки {longRow.ColumnDefinitions[2].ActualWidth}");
        });
    }
}
