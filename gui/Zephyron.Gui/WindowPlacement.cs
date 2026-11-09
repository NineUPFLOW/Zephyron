using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Zephyron.Core;

namespace Zephyron.Gui;

/// <summary>
/// Положение главного окна: прямоугольник (физические пиксели) и «развёрнуто» — в config\window.json.
/// </summary>
/// <remarks>
/// Развёрнутое окно без запомненного прямоугольника открывалось на чужом мониторе. Теперь
/// перед показом окно ставится на сохранённое место, а оно зажимается в ныне подключённые
/// мониторы (отключили второй — окно вернётся на тот, что остался).
/// </remarks>
internal static class WindowPlacement
{
    public static string DefaultPath => Path.Combine("config", "window.json");

    private sealed record Saved(int Left, int Top, int Width, int Height, bool Maximized);

    private const uint SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>Есть ли файл с сохранённым местом; читается один раз при создании окна.</summary>
    public static bool TryLoad(out bool maximized, string? path = null)
    {
        maximized = false;
        var saved = Read(path);

        if (saved is null)
            return false;

        maximized = saved.Maximized;
        return true;
    }

    /// <summary>Ставит окно (ещё не показанное, HWND уже есть) на сохранённое место.</summary>
    public static void Apply(Window window, string? path = null)
    {
        var hwnd = new WindowInteropHelper(window).Handle;

        if (Read(path) is not { } saved || hwnd == IntPtr.Zero)
            return;

        var rect = Clamp(new System.Drawing.Rectangle(saved.Left, saved.Top, saved.Width, saved.Height),
            System.Windows.Forms.Screen.AllScreens.Select(s => s.WorkingArea).ToList());

        SetWindowPos(hwnd, IntPtr.Zero, rect.Left, rect.Top, rect.Width, rect.Height, SwpNoZOrder | SwpNoActivate);
    }

    /// <summary>Записывает место окна; свёрнутое не пишем — вернётся прежнее.</summary>
    public static void Save(Window window, string? path = null)
    {
        if (window.WindowState == WindowState.Minimized || !window.IsLoaded)
            return;

        try
        {
            var bounds = window.WindowState == WindowState.Normal
                ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)
                : window.RestoreBounds;

            if (bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1)
                return;

            var dpi = VisualTreeHelper.GetDpi(window);
            var saved = new Saved(
                (int)Math.Round(bounds.Left * dpi.DpiScaleX), (int)Math.Round(bounds.Top * dpi.DpiScaleY),
                (int)Math.Round(bounds.Width * dpi.DpiScaleX), (int)Math.Round(bounds.Height * dpi.DpiScaleY),
                window.WindowState == WindowState.Maximized);

            var target = path ?? DefaultPath;

            if (Path.GetDirectoryName(Path.GetFullPath(target)) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            AtomicFile.WriteAllText(target, JsonSerializer.Serialize(saved));
        }
        catch (Exception)
        {
            // Не записалось — в следующий раз окно откроется по умолчанию.
        }
    }

    /// <summary>
    /// Прямоугольник, целиком лежащий в рабочей области монитора, с которым он пересекается
    /// больше всего (ни с одним — первый монитор); размер не больше области.
    /// </summary>
    internal static System.Drawing.Rectangle Clamp(System.Drawing.Rectangle rect, IReadOnlyList<System.Drawing.Rectangle> areas)
    {
        if (areas.Count == 0)
            return rect;

        var area = areas.OrderByDescending(a => { var i = System.Drawing.Rectangle.Intersect(a, rect); return (long)i.Width * i.Height; }).First();
        int width = Math.Min(rect.Width, area.Width), height = Math.Min(rect.Height, area.Height);
        int left = Math.Clamp(rect.Left, area.Left, area.Right - width), top = Math.Clamp(rect.Top, area.Top, area.Bottom - height);

        return new System.Drawing.Rectangle(left, top, width, height);
    }

    private static Saved? Read(string? path)
    {
        try
        {
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(path ?? DefaultPath));

            return saved is { Width: >= 200, Height: >= 200 } ? saved : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
