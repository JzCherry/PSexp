using System.Runtime.InteropServices;
using Avalonia;
using SkiaSharp;

namespace PsExporter.Views;

internal sealed class WindowPickOutline(SKColor accent) : IDisposable
{
    private readonly LayeredDesktopOverlay _overlay = new();
    private PixelRect? _bounds;
    private uint _dpi;

    internal void Follow(IntPtr window)
    {
        if (window == IntPtr.Zero
            || (DwmGetWindowAttribute(window, 9, out var rect, Marshal.SizeOf<WindowRect>()) != 0
                && !GetWindowRect(window, out rect))) // DWMWA_EXTENDED_FRAME_BOUNDS
        {
            Hide();
            return;
        }

        Show(new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top),
            GetDpiForWindow(window));
    }

    internal void Show(PixelRect bounds, uint dpi)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            Hide();
            return;
        }
        if (_bounds == bounds && _dpi == dpi) return;

        var scale = dpi == 0 ? 1f : dpi / 96f;
        var inset = Math.Min(3f * scale, (Math.Min(bounds.Width, bounds.Height) - 1) / 2f);
        var radius = 10f * scale;
        using var surface = _overlay.CreateSurface(bounds.Width, bounds.Height);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var frame = new SKRect(inset, inset, bounds.Width - inset, bounds.Height - inset);
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };
        // 白色细边让蓝框在浅色、深色和同色窗口边缘都能辨认。
        paint.Color = SKColors.White;
        paint.StrokeWidth = 5f * scale;
        canvas.DrawRoundRect(frame, radius, radius, paint);
        paint.Color = accent;
        paint.StrokeWidth = 3f * scale;
        canvas.DrawRoundRect(frame, radius, radius, paint);
        surface.Flush();
        _overlay.Present(bounds);
        _bounds = bounds;
        _dpi = dpi;
    }

    internal void Hide()
    {
        _overlay.Hide();
        _bounds = null;
    }

    public void Dispose() => _overlay.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect { internal int Left, Top, Right, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out WindowRect value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out WindowRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
}
