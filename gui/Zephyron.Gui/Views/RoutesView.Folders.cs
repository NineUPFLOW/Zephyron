using System.Windows;
using System.Windows.Controls;
using Zephyron.Core;
using Zephyron.Core.Rules;
using Zephyron.Core.Services;
using Zephyron.Proxy;

namespace Zephyron.Gui.Views;

/// <summary>
/// Папки «Маршрутов» по макету владельца 13.10: «Добавить папку», «⋯» у папки
/// и «+ Добавить маршрут в эту папку».
/// </summary>
/// <remarks>
/// Всё это — вид (<see cref="RouteFolders"/>): маршруты, их правила и путь
/// не меняются, перезапуск не нужен. Папка каталога («Discord») переименовывается
/// подписью (<see cref="AppSettings.FolderLabels"/>): её имя — ключ перекладки.
/// </remarks>
public partial class RoutesView
{
    /// <summary>Папка, в которую ляжет следующий добавленный свой маршрут; <c>null</c> — никуда.</summary>
    private string? _addInto;

    /// <summary>Путь всего сервиса: то же окно пути, выбор — всем частям сразу.</summary>
    private void OnFolderRoute(object sender, RoutedEventArgs e)
    {
        // Кнопка внутри шапки папки: без этого щелчок ещё и свернул бы папку.
        e.Handled = true;

        if (sender is FrameworkElement { DataContext: ServiceRow folder })
            Dispatcher.BeginInvoke(() => AskGroupPath(folder), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void AskGroupPath(ServiceRow folder)
    {
        var parts = folder.Routable;

        if (parts.Count == 0)
            return;

        static PathPick Pick(PartRow row) => !row.Explicit && row.DefaultLabel is not null
            ? PathPick.Default
            : row.Applied switch { 0 => PathPick.Direct, 1 => PathPick.Desync, PartRow.Off => PathPick.Off, _ => PathPick.Proxy };

        var picks = parts.Select(Pick).Distinct().ToList();
        var defaults = parts.Select(p => p.DefaultLabel).Distinct().ToList();

        var window = new PathWindow(folder.Shown + " — все части", picks.Count == 1 ? picks[0] : PathPick.Default,
            parts.Select(p => p.PoolId).Distinct().Count() == 1 ? parts[0].PoolId : null,
            defaults.Count == 1 ? defaults[0] : "у каждой части своё",
            canRecipe: false,
            "Чинит пресет. Свой рецепт части останется, если она уже на десинке.", _pools)
        {
            Owner = Window.GetWindow(this),
            PoolsHint = _path == TunnelPath.Warp ? "Туннель идёт через WARP: пулы серверов работают с VPN."
                : !_tunnelOn ? "Режим «Десинк»: туннеля нет, пока режим не сменён на «Главной», такой маршрут идёт через десинк." : null,
        };

        if (window.ShowDialog() != true)
            return;

        try
        {
            var file = UserRulesFile.Load();
            int changed = 0;

            foreach (var row in parts)
            {
                if (RouteKeys.Parse(row.Key) is not var (kind, value))
                    continue;

                var match = RouteKeys.MatchOf(kind, value);
                var within = ServiceCatalog.All.SelectMany(s => s.Parts)
                    .FirstOrDefault(p => string.Equals(p.List, value, StringComparison.OrdinalIgnoreCase))?.Within;

                switch (window.Picked)
                {
                    case PathPick.Default:
                        if (row.Explicit && file.Remove(match, value))
                            changed++;
                        break;

                    case PathPick.Direct:
                        file.Set(match, value, RoutingMode.Direct, recipe: null, before: within);
                        changed++;
                        break;

                    // Часть уже на десинке со своим рецептом — его не трогаем: выбран путь, а не рецепт.
                    case PathPick.Desync when row.Explicit && row.Applied == 1:
                        break;

                    case PathPick.Desync:
                        file.Set(match, value, RoutingMode.Desync, recipe: null, before: within);
                        changed++;
                        break;

                    case PathPick.Proxy:
                        file.Set(match, value, RoutingMode.Proxy, recipe: null, before: within,
                            server: window.Pool is { } pool ? ServerPools.RuleServer(pool) : null);
                        changed++;
                        break;

                    case PathPick.Off:
                        file.SetOff(match, value, before: within);
                        changed++;
                        break;
                }
            }

            if (changed == 0)
                return;

            file.Save();
            Reload();

            Status.Text = $"«{folder.Shown}»: путь сменён у частей — {changed}. Вступит в силу после следующего запуска движков.";
            this.Offer("Маршрут изменён");
        }
        catch (Exception ex)
        {
            Status.Text = "Не записалось: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>«Добавить папку»: пустая папка, куда перетаскивают маршруты.</summary>
    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var window = new FolderNameWindow("Папка без названия", Taken()) { Owner = Window.GetWindow(this) };

        if (window.ShowDialog() != true || window.Chosen is not { } name)
            return;

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            (settings with { MadeFolders = [.. settings.MadeFolders, name] }).Save(AppSettings.DefaultPath);

            _open.Add(name);
            Search.Text = string.Empty;
            Reload();

            Status.Text = $"Папка «{name}» — наверху списка. Перетащите в неё маршруты или добавьте свой кнопкой в ней.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не записалось: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>«+ Добавить маршрут в эту папку»: поле поиска ждёт сайт, адрес или подсеть.</summary>
    private void OnAddIntoFolder(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ServiceRow folder })
            return;

        _addInto = folder.Name;
        Search.Text = string.Empty;
        Search.Focus();

        Status.Text = $"Впишите сайт, адрес или подсеть в поле выше — новый маршрут ляжет в папку «{folder.Shown}». "
            + "Существующий маршрут — перетащите на папку.";
    }

    /// <summary>
    /// Только что добавленный свой маршрут — в папку, из которой его добавляли.
    /// </summary>
    private void PlaceAdded(string key)
    {
        if (_addInto is not { } folder)
            return;

        _addInto = null;

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            (settings with { RouteFolders = RouteFolders.Move(settings.RouteFolders, key, folder, null) })
                .Save(AppSettings.DefaultPath);

            _open.Add(folder);
        }
        catch (Exception)
        {
            // Не легло в папку — маршрут всё равно записан, просто стоит сам по себе.
        }
    }

    /// <summary>«⋯» в шапке папки.</summary>
    private void OnFolderMore(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is not FrameworkElement { DataContext: ServiceRow folder } anchor)
            return;

        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };

        if (folder.Program is { } program)
        {
            menu.Items.Add(MenuEntry("", "Удалить программу…", () => RemoveProgram(program)));
            menu.IsOpen = true;
            return;
        }

        menu.Items.Add(MenuEntry("", "Проверить автоподбором", () => PickFolder(folder)));
        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuLine") });
        menu.Items.Add(MenuEntry("", "Задать другое имя…", () => Rename(folder)));
        menu.Items.Add(MenuEntry("", "Добавить маршрут…", () => OnAddIntoFolder(anchor, e)));
        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuLine") });
        menu.Items.Add(MenuEntry("", folder.Made ? "Удалить папку" : "Разобрать папку", () => Dissolve(folder)));

        menu.IsOpen = true;
    }

    /// <summary>
    /// Автоподбор на все маршруты папки сразу — как «Автоподбор» сверху, только её имена
    /// (просьба пользователя 28.10: у Discord семь маршрутов, руками их не проверить).
    /// </summary>
    private void PickFolder(ServiceRow folder)
    {
        var lists = folder.Parts
            .Select(p => RouteKeys.Parse(p.Key)?.Value)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (lists.Count == 0)
        {
            Status.Text = $"В «{folder.Shown}» нет маршрутов по именам — проверять автоподбором нечего.";
            return;
        }

        var picker = new AutoPickWindow { Owner = Window.GetWindow(this) };
        picker.PickListsOnOpen(lists);
        picker.ShowDialog();
        Reload();
    }

    private void OnRouteExpand(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if ((sender as FrameworkElement)?.DataContext is PartRow part)
            part.ShowEntries = !part.ShowEntries;
    }

    /// <summary>Автоподбор одного маршрута — кнопкой в его строке.</summary>
    private void OnRoutePick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is not FrameworkElement { Tag: string key } || RouteKeys.Parse(key)?.Value is not { } value)
            return;

        var picker = new AutoPickWindow { Owner = Window.GetWindow(this) };
        picker.PickListsOnOpen(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { value });
        picker.ShowDialog();
        Reload();
    }

    private void Rename(ServiceRow folder)
    {
        var window = new FolderNameWindow($"Переименовать «{folder.Shown}»", Taken(except: folder.Name), folder.Shown, "Сохранить имя")
        {
            Owner = Window.GetWindow(this),
        };

        if (window.ShowDialog() != true || window.Chosen is not { } name)
            return;

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            var labels = new Dictionary<string, string>(settings.FolderLabels, StringComparer.OrdinalIgnoreCase);

            if (string.Equals(name, folder.Name, StringComparison.Ordinal))
                labels.Remove(folder.Name);
            else
                labels[folder.Name] = name;

            (settings with { FolderLabels = labels }).Save(AppSettings.DefaultPath);
            Reload();

            Status.Text = $"Папка «{folder.Shown}» теперь «{name}».";
        }
        catch (Exception ex)
        {
            Status.Text = "Не записалось: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Распустить папку: её маршруты — сами по себе; заведённая человеком — исчезает.
    /// </summary>
    /// <remarks>
    /// Без вопроса: маршруты не пропадают и путь не меняют, а вернуть каждый
    /// на место можно «⋯» в его строке.
    /// </remarks>
    private void Dissolve(ServiceRow folder)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            var moved = settings.RouteFolders;

            foreach (var part in (_all.FirstOrDefault(s => s.Name == folder.Name && s.IsFolder) ?? folder).Parts)
                moved = RouteFolders.Move(moved, part.Key, string.Empty, part.HomeFolder);

            var labels = new Dictionary<string, string>(settings.FolderLabels, StringComparer.OrdinalIgnoreCase);
            labels.Remove(folder.Name);

            (settings with
            {
                RouteFolders = moved,
                MadeFolders = settings.MadeFolders.Where(f => !string.Equals(f, folder.Name, StringComparison.OrdinalIgnoreCase)).ToList(),
                FolderLabels = labels,
                RouteOrder = settings.RouteOrder.Where(k => k != FolderPlace(folder.Name)).ToList(),
            }).Save(AppSettings.DefaultPath);

            _open.Remove(folder.Name);
            Reload();

            Status.Text = folder.Parts.Count == 0
                ? $"Папка «{folder.Shown}» удалена."
                : $"Папки «{folder.Shown}» больше нет, её маршруты — сами по себе. Путь у них прежний.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не записалось: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Ключ строки сменился (свой домен или адрес стал списком) — перекладка
    /// и место в порядке переходят к новому ключу, иначе строка выпала бы из папки.
    /// </summary>
    private static void RenameKey(string from, string to)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            var moved = new Dictionary<string, string>(settings.RouteFolders, StringComparer.Ordinal);

            if (moved.Remove(from, out var folder))
                moved[to] = folder;

            (settings with
            {
                RouteFolders = moved,
                RouteOrder = settings.RouteOrder.Select(k => k == from ? to : k).ToList(),
            }).Save(AppSettings.DefaultPath);
        }
        catch (Exception)
        {
            // Не перенеслось — строка встанет на место по умолчанию, маршрут цел.
        }
    }

    /// <summary>Имена, которые новой папке брать нельзя: программы и уже показанные папки.</summary>
    private IReadOnlyCollection<string> Taken(string? except = null) =>
        ProgramNames()
            .Concat(_all.Where(s => s.IsFolder && s.Name != except).SelectMany(s => new[] { s.Name, s.Shown }))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
