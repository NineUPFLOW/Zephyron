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

    /// <summary>
    /// Ширина «Главной» не зависит от состояния: страница во всю ширину предела, по центру.
    /// </summary>
    /// <remarks>
    /// 26.10, видео владельца: с HorizontalAlignment.Center страница сжималась до
    /// ширины содержимого — «Выключен» уже, «Работает» шире, — и прыгала при каждой
    /// смене состояния, а в узкой колонке карточки вытягивались и низ уходил за край.
    /// </remarks>
    [Fact]
    public void The_page_keeps_its_width_whatever_the_state()
    {
        Sta.Run(() =>
        {
            var view = new StatusView();
            view.Page.MaxWidth = 1120;

            (double Width, double Row) Size(HomeState state, string hint, double width = 1900)
            {
                view.Present(state, hint, state, state);
                view.Measure(new System.Windows.Size(width, 1000));
                view.Arrange(new System.Windows.Rect(0, 0, width, 1000));
                view.UpdateLayout();
                return (view.Page.ActualWidth, view.TopRow.ActualHeight);
            }

            var off = Size(HomeState.Off, "Обход не работает");
            var running = Size(HomeState.Running, "Маршруты — через туннель, остальное — напрямую, закрытое чинит десинк.");
            var failed = Size(HomeState.Failed, string.Join(" ", Enumerable.Repeat("Движок туннеля не поднялся, супервизор перестал его поднимать.", 4)));

            Assert.Equal(System.Windows.HorizontalAlignment.Stretch, view.Page.HorizontalAlignment);
            Assert.Equal(1120, off.Width, 1);
            Assert.Equal(off.Width, running.Width, 1);

            // Высота ряда с плашкой тоже одна: время, строка счётчиков и длинное
            // пояснение не делают плашку выше «Компонентов» рядом.
            Assert.Equal(off.Row, running.Row, 1);
            Assert.Equal(off.Row, failed.Row, 1);

            // И в узкой раскладке, где плашка во всю ширину и «Компоненты» её не выравнивают.
            var narrowOff = Size(HomeState.Off, "Обход не работает", 900);
            var narrowRunning = Size(HomeState.Running, "Маршруты — через туннель, остальное — напрямую, закрытое чинит десинк.", 900);
            Assert.Equal(narrowOff.Row, narrowRunning.Row, 1);
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
