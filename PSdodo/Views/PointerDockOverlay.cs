using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using PsExporter.Controls;
using PsExporter.Services;
using SkiaSharp;

namespace PsExporter.Views;

/// <summary>Windows per-pixel layered window, independent of Avalonia's DirectComposition surface.</summary>
internal sealed class PointerDockOverlay : IDisposable
{
    private readonly IntPtr _window;
    private readonly IntPtr _dc;
    private IntPtr _dib, _originalBitmap, _pixels;
    private int _width, _height;
    private bool _visible;
    private GenieTexture? _texture;
    private SKPoint[]? _textureCoordinates;
    private int _textureWidth, _textureHeight;
    private GenieFrameTiming _timing = new();

    internal string FrameTimingSummary => _timing.Summary;

    internal PointerDockOverlay()
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

    internal void Prepare(SKBitmap bitmap)
    {
        Clear();
        _texture = new GenieTexture(bitmap);
        _textureWidth = bitmap.Width;
        _textureHeight = bitmap.Height;
        BeginPlaybackTiming();
    }

    internal void BeginPlaybackTiming() => _timing = new GenieFrameTiming();

    internal bool Draw(EdgeSnapHelper.GenieMeshData mesh, PixelPoint origin, double scale, double opacity)
    {
        var alpha = (byte)Math.Round(opacity * 255);
        if (alpha == 0)
        {
            Hide();
            return false;
        }

        var vertices = mesh.Vertices.AsSpan();
        float minX = vertices[0].X, maxX = minX, minY = vertices[0].Y, maxY = minY;
        foreach (var vertex in vertices)
        {
            minX = Math.Min(minX, vertex.X);
            maxX = Math.Max(maxX, vertex.X);
            minY = Math.Min(minY, vertex.Y);
            maxY = Math.Max(maxY, vertex.Y);
        }
        var left = (int)Math.Floor(minX * scale) - 1;
        var top = (int)Math.Floor(minY * scale) - 1;
        var width = (int)Math.Ceiling(maxX * scale) - left + 1;
        var height = (int)Math.Ceiling(maxY * scale) - top + 1;
        EnsureBuffer(width, height);

        _textureCoordinates ??= Array.ConvertAll(mesh.TexCoords, point => new SKPoint(
            point.X * _textureWidth / mesh.SourceWidth, point.Y * _textureHeight / mesh.SourceHeight));
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul),
            _pixels, _width * 4) ?? throw new InvalidOperationException("Cannot create dock animation surface.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(-left, -top);
        canvas.Scale((float)scale);
        var paint = _texture!.Paint;
        paint.Color = SKColors.White.WithAlpha(alpha);
        canvas.DrawVertices(SKVertexMode.Triangles, mesh.Vertices, _textureCoordinates, null, mesh.Indices, paint);
        surface.Flush();

        var destination = new Pair(origin.X + left, origin.Y + top);
        var size = new Pair(width, height);
        var source = new Pair(0, 0);
        var blend = new Blend { Alpha = 255, Format = 1 };
        if (!UpdateLayeredWindow(_window, IntPtr.Zero, ref destination, ref size, _dc, ref source, 0, ref blend, 2))
            throw new Win32Exception();
        if (!_visible)
        {
            ShowWindow(_window, 4); // SW_SHOWNOACTIVATE. The first pixels are already installed.
            _visible = true;
        }
        _timing.Drawn();
        return true;
    }

    internal static async Task<bool> WaitForDesktopAsync()
    {
        // 仅在交接时等待桌面合成，不能把 DwmFlush 放进逐帧绘制或阻塞 UI 线程。
        var flush = Task.Run(DwmFlush);
        return await Task.WhenAny(flush, Task.Delay(60)) == flush && await flush == 0;
    }

    internal static async Task WaitForFrameAsync()
    {
        // 分层窗由桌面合成器提交。渲染完再等下一次提交，避免固定延时叠加在绘制耗时上。
        var started = Stopwatch.GetTimestamp();
        var result = await Task.Run(DwmFlush).ConfigureAwait(false);
        if (result != 0 || Stopwatch.GetElapsedTime(started).TotalMilliseconds < 2)
            await Task.Delay(8).ConfigureAwait(false);
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

    private void Hide()
    {
        if (_visible) ShowWindow(_window, 0);
        _visible = false;
    }

    internal void Clear()
    {
        Hide();
        _texture?.Dispose();
        _texture = null;
        _textureCoordinates = null;
    }

    public void Dispose()
    {
        Clear();
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
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, uint attribute, ref int value, int size);
    [DllImport("gdi32.dll", EntryPoint = "CreateCompatibleDC", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", EntryPoint = "DeleteDC")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", EntryPoint = "CreateDIBSection", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapHeader header, uint usage, out IntPtr pixels, IntPtr section, uint offset);
    [DllImport("gdi32.dll", EntryPoint = "SelectObject")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")] private static extern bool DeleteObject(IntPtr obj);
}
