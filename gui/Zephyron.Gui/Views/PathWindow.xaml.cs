using System.Windows;
using System.Windows.Controls;
using Zephyron.Core;

namespace Zephyron.Gui.Views;

/// <summary>Что выбрано в окне пути.</summary>
public enum PathPick
{
    /// <summary>Снять своё правило — как задано заводскими.</summary>
    Default,

    Direct,

    Desync,

    Proxy,

    /// <summary>Выключить часть целиком (<see cref="Zephyron.Core.Rules.RoutingRule.Off"/>).</summary>
    Off,
}

/// <summary>
/// Путь маршрута отдельным окном: выключено, напрямую, десинк с рецептом,
/// через выход с пулом серверов.
/// </summary>
/// <remarks>
/// <para>
/// Владелец 19.10 (идеи №6): «выбор пути — отдельным окном; десинк раскрывает
/// рецепты, через выход — пулы». Пулов в меню пути было не вместить: их у
/// человека сколько угодно, и у каждого своё — сколько серверов, кто закреплён.
/// </para>
/// <para>
/// Рецепт выбирается прежним окном рецептов (<see cref="RecipeWindow"/>):
/// оно проверяет рецепты на живом имени, и переносить эту проверку сюда
/// значило бы держать её в двух местах. Отсюда оно открывается кнопкой
/// «Рецепт…» у десинка.
/// </para>
/// </remarks>
public partial class PathWindow : Window
{
    public PathPick Picked { get; private set; }

    /// <summary>Пул при «через выход»; <c>null</c> — пул по умолчанию.</summary>
    public string? Pool { get; private set; }

    /// <summary>Нажато «Рецепт…» — выбрать рецепт десинка следом.</summary>
    public bool RecipeWanted { get; private set; }

    private readonly string? _defaultLabel;
    private readonly bool _canRecipe;
    private readonly string _desyncNote;
    private readonly IReadOnlyList<ServerPool> _pools;

    /// <summary>«Selective Routing» включён — под «через выход» выбирается пул.</summary>
    private readonly bool _poolsOn;

    /// <param name="title">Имя маршрута.</param>
    /// <param name="current">Что стоит сейчас.</param>
    /// <param name="pool">Пул маршрута «через выход»; <c>null</c> — пул по умолчанию.</param>
    /// <param name="defaultLabel">
    /// Куда маршрут шёл бы без своего правила; <c>null</c> — умолчания у него нет (свой маршрут).
    /// </param>
    /// <param name="canRecipe">Есть ли маршруту рецепт: у списков адресов его нет (владелец 19.10).</param>
    /// <param name="desyncNote">Подпись десинка: каким рецептом он идёт сейчас.</param>
    /// <param name="pools">
    /// Пулы серверов с вкладки VPN; <c>null</c> — «Selective Routing» выключен, и пул не выбирается.
    /// </param>
    public PathWindow(
        string title,
        PathPick current,
        string? pool,
        string? defaultLabel,
        bool canRecipe,
        string desyncNote,
        IReadOnlyList<ServerPool>? pools)
    {
        InitializeComponent();

        // Окно растёт вниз по ходу подбора: высота не больше рабочей области, низ не уходит за панель задач.
        MaxHeight = Math.Min(MaxHeight, SystemParameters.WorkArea.Height - 40);
        SizeChanged += (_, _) => KeepOnScreen();

        _pools = pools ?? [];
        _poolsOn = pools is not null;

        Head.Text = $"Маршрут: {title}";
        Picked = current;
        Pool = ServerPools.Find(_pools, pool)?.Id;
        _defaultLabel = defaultLabel;
        _canRecipe = canRecipe;
        _desyncNote = desyncNote;

        Rebuild();
    }

    private void Rebuild()
    {
        Options.Children.Clear();

        if (_defaultLabel is { } fallback)
        {
            Options.Children.Add(Card(PathPick.Default, $"По умолчанию — {fallback}",
                "Своего правила нет: маршрут идёт, как задано заводскими правилами."));
        }

        Options.Children.Add(Card(PathPick.Direct, "Напрямую", "Напрямую и без десинка — как будто программы нет."));

        Options.Children.Add(Card(PathPick.Desync, "Десинк", _desyncNote,
            Picked == PathPick.Desync && _canRecipe ? RecipeButton() : null));

        Options.Children.Add(Card(PathPick.Proxy, "Через выход", ProxyNote(),
            Picked == PathPick.Proxy && _poolsOn ? PoolList() : null));

        Options.Children.Add(Card(PathPick.Off, "Отключено",
            "Не действует ни этот маршрут, ни заводское правило для того же списка: имена решаются другими правилами."));
    }

    /// <summary>Что сказать вместо пулов, когда они не действуют (выход WARP); <c>null</c> — как обычно.</summary>
    public string? PoolsHint { get; init; }

    private string ProxyNote() => PoolsHint is not null ? PoolsHint
        : ServerPools.Find(_pools, Pool) is { } pool
        ? $"Пул «{pool.Name}»."
        : _pools.Count == 0
            ? "Через серверы подписок: лучший по замеру или выбранный во вкладке VPN."
            : "Общий пул — все серверы.";

    private Button Card(PathPick pick, string head, string note, UIElement? inside = null)
    {
        bool chosen = Picked == pick;

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = head, FontWeight = FontWeights.SemiBold, FontSize = 15 });

        var caption = new TextBlock { Text = note, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        caption.SetResourceReference(StyleProperty, "Caption");
        text.Children.Add(caption);

        if (inside is not null)
            text.Children.Add(inside);

        var mark = new TextBlock
        {
            Text = chosen ? "" : string.Empty,
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12, 2, 0, 0),
        };
        mark.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        mark.SetResourceReference(TextBlock.ForegroundProperty, "Accent");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        Grid.SetColumn(mark, 1);
        grid.Children.Add(mark);

        var button = new Button { Content = grid };
        button.SetResourceReference(StyleProperty, "Choice");
        System.Windows.Controls.Primitives.Selector.SetIsSelected(button, chosen);

        button.Click += (_, e) =>
        {
            // Нажатие внутри раскрытого (пул, «Рецепт…») — их дело.
            if (e.OriginalSource != button)
                return;

            Picked = pick;
            Rebuild();
        };

        return button;
    }

    private UIElement RecipeButton()
    {
        var button = new Button { Content = "Рецепт…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        button.SetResourceReference(StyleProperty, "Ghost");
        button.Click += (_, e) =>
        {
            e.Handled = true;
            Picked = PathPick.Desync;
            RecipeWanted = true;
            DialogResult = true;
        };

        return button;
    }

    /// <summary>Пулы под «через выход»: пул по умолчанию и свои.</summary>
    private UIElement PoolList()
    {
        var list = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };

        list.Children.Add(PoolCard(null, "Общий пул", "Все рабочие серверы подписок."));

        foreach (var pool in _pools)
        {
            var note = pool.Servers.Count == 0
                ? "Пусто: пока в пуле нет серверов, маршрут пойдёт через общий пул."
                : $"{Servers(pool.Servers.Count)} · " + (pool.Preferred is { } pinned ? $"закреплён «{pinned}»" : "автоподбор");

            list.Children.Add(PoolCard(pool.Id, pool.Name, note));
        }

        if (_pools.Count == 0)
        {
            var hint = new TextBlock
            {
                Text = "Свои пулы — папки с серверами, например «Россия» или «Скоростной» — создаются во вкладке VPN.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 2, 0, 0),
            };
            hint.SetResourceReference(StyleProperty, "Caption");
            list.Children.Add(hint);
        }

        return list;
    }

    private Button PoolCard(string? id, string head, string note)
    {
        bool chosen = Pool == id;

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = head, FontWeight = FontWeights.SemiBold });

        var caption = new TextBlock { Text = note, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
        caption.SetResourceReference(StyleProperty, "Caption");
        text.Children.Add(caption);

        var button = new Button { Content = text, Padding = new Thickness(14, 9, 14, 9), Margin = new Thickness(0, 0, 0, 6) };
        button.SetResourceReference(StyleProperty, "Choice");
        System.Windows.Controls.Primitives.Selector.SetIsSelected(button, chosen);

        button.Click += (_, e) =>
        {
            e.Handled = true;
            Pool = id;
            Picked = PathPick.Proxy;
            Rebuild();
        };

        return button;
    }

    private static string Servers(int count) => (count % 10, count % 100) switch
    {
        (1, not 11) => $"{count} сервер",
        (2 or 3 or 4, not (12 or 13 or 14)) => $"{count} сервера",
        _ => $"{count} серверов",
    };

    private void OnDone(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>Поднимает окно, если его низ ушёл за рабочую область.</summary>
    private void KeepOnScreen()
    {
        var bottom = SystemParameters.WorkArea.Bottom - 20;

        if (Top + ActualHeight > bottom)
            Top = Math.Max(SystemParameters.WorkArea.Top, bottom - ActualHeight);
    }
}
