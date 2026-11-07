using System.Diagnostics;
using System.Windows;

namespace Zephyron.Gui;

/// <summary>Открыть ссылку в браузере; не вышло — скопировать её и сказать об этом.</summary>
internal static class Links
{
    public static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true })?.Dispose();
        }
        catch (Exception)
        {
            string copied;

            try
            {
                Clipboard.SetText(url);
                copied = "Ссылка скопирована — вставьте её в адресную строку браузера.";
            }
            catch (Exception)
            {
                copied = "Откройте её вручную.";
            }

            MessageBox.Show($"Браузер не открылся.\n\n{url}\n\n{copied}", "Zephyron", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
