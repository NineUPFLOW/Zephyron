using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Zephyron.Core;

namespace Zephyron.Gui;

/// <summary>Кнопка «Поддержать» везде одна: куда идти, человек выбирает в меню.</summary>
internal static class DonateMenu
{
    public static void Show(object sender)
    {
        if (sender is not FrameworkElement anchor)
            return;

        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };

        menu.Items.Add(Entry("Tribute", About.Support));
        menu.Items.Add(Entry("tips.tips", About.SupportTips));
        menu.IsOpen = true;
    }

    private static MenuItem Entry(string name, string url)
    {
        var item = new MenuItem { Header = name };

        item.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception)
            {
                // Нет браузера по умолчанию — обе ссылки есть в README.
            }
        };

        return item;
    }
}
