using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using SkiaSharp;

namespace PsExporter.Views;

/// <summary>Click-through Windows per-pixel layered window.</summary>
internal sealed class LayeredDesktopOverlay : IDisposable
{
    private readonly IntPtr _window;
    private readonly IntPtr _dc;
    private IntPtr _dib, _originalBitmap, _pixels;
    private int _width, _height;
    private bool _visible;

    internal LayeredDesktopOverlay()
    {
        const uint layered = 0x80000, transparent = 0x20, noActivate = 0x08000000, toolWindow = 0x80, topmost = 0x8;
        const uint extendedStyle = layered | transparent | noActivate | toolWindow | topmost;
        _window = CreateWindowEx(extendedStyle, "STATIC", null, 0x80000000, 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_window == IntPtr.Zero) throw new Win32Exception();
        var disabled = 1;
        _ = DwmSetWindowAttribute(_window, 3, ref disabled, sizeof(int)); // DWMWA_TRANSITIONS_FORCEDISABLED
        _dc = CreateCompatibleDC(IntPtr.Zero);
        if (_dc == IntPtr.Zero)
        {
            var error = new Win32Exception();
            DestroyWindow(_window);
            throw error;
        }
    }

    internal SKSurface CreateSurface(int width, int height)
    {
        EnsureBuffer(width, height);
        return SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul),
            _pixels, _width * 4) ?? throw new InvalidOperationException("Cannot create layered window surface.");
    }

    internal void Present(PixelRect bounds)
    {
        var destination = new Pair(bounds.X, bounds.Y);
        var size = new Pair(bounds.Width, bounds.Height);
        var source = new Pair(0, 0);
        var blend = new Blend { Alpha = 255, Format = 1 };
        if (!UpdateLayeredWindow(_window, IntPtr.Zero, ref destination, ref size, _dc, ref source, 0, ref blend, 2))
            throw new Win32Exception();
        if (!_visible)
        {
            ShowWindow(_window, 4); // SW_SHOWNOACTIVATE. The first pixels are already installed.
            _visible = true;
        }
    }

    private void EnsureBuffer(int width, int height)
    {
        if (width <= _width && height <= _height) return;
        // Grow in chunks so the expanding mesh does not allocate a bitmap every frame.
        width = Math.Max(_width, (width + 63) / 64 * 64);
        height = Math.Max(_height, (height + 63) / 64 * 64);
        var header = new BitmapHeader { Size = 40, Width = width, Height = -height, Planes = 1, Bits = 32 };
        var bitmap = CreateDIBSection(_dc, ref header, 0, out var pixels, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero) throw new Win32Exception();
        var previous = SelectObject(_dc, bitmap);
        if (previous == IntPtr.Zero)
        {
            DeleteObject(bitmap);
            throw new InvalidOperationException("Cannot select dock animation bitmap.");
        }
        if (_dib == IntPtr.Zero) _originalBitmap = previous;
        else DeleteObject(_dib);
        _dib = bitmap;
        _pixels = pixels;
        _width = width;
        _height = height;
    }

    internal void Hide()
    {
        if (_visible) ShowWindow(_window, 0);
        _visible = false;
    }

    public void Dispose()
    {
        Hide();
        if (_dib != IntPtr.Zero)
        {
            SelectObject(_dc, _originalBitmap);
            DeleteObject(_dib);
        }
        DeleteDC(_dc);
        DestroyWindow(_window);
    }

    [StructLayout(LayoutKind.Sequential)] private record struct Pair(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct Blend { internal byte Op, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapHeader
    {
        internal uint Size;
        internal int Width, Height;
        internal ushort Planes, Bits;
        internal uint Compression, ImageSize;
        internal int XResolution, YResolution;
        internal uint ColorsUsed, ImportantColors;
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string? title, uint style, int x, int y,
        int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll", EntryPoint = "UpdateLayeredWindow", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destinationDc, ref Pair destination, ref Pair size,
        IntPtr sourceDc, ref Pair source, uint colorKey, ref Blend blend, uint flags);
    [DllImport("user32.dll", EntryPoint = "ShowWindow")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", EntryPoint = "DestroyWindow")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, uint attribute, ref int value, int size);
    [DllImport("gdi32.dll", EntryPoint = "CreateCompatibleDC", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", EntryPoint = "DeleteDC")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", EntryPoint = "CreateDIBSection", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapHeader header, uint usage, out IntPtr pixels, IntPtr section, uint offset);
    [DllImport("gdi32.dll", EntryPoint = "SelectObject")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")] private static extern bool DeleteObject(IntPtr obj);
}
