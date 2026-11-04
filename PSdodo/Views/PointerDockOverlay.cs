using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using PsExporter.Controls;
using PsExporter.Services;
using SkiaSharp;

namespace PsExporter.Views;

internal sealed class PointerDockOverlay : IDisposable
{
    private readonly LayeredDesktopOverlay _overlay = new();
    private GenieTexture? _texture;
    private SKPoint[]? _textureCoordinates;
    private int _textureWidth, _textureHeight;
    private GenieFrameTiming _timing = new();

    internal string FrameTimingSummary => _timing.Summary;

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
            _overlay.Hide();
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

        _textureCoordinates ??= Array.ConvertAll(mesh.TexCoords, point => new SKPoint(
            point.X * _textureWidth / mesh.SourceWidth, point.Y * _textureHeight / mesh.SourceHeight));
        using var surface = _overlay.CreateSurface(width, height);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(-left, -top);
        canvas.Scale((float)scale);
        var paint = _texture!.Paint;
        paint.Color = SKColors.White.WithAlpha(alpha);
        canvas.DrawVertices(SKVertexMode.Triangles, mesh.Vertices, _textureCoordinates, null, mesh.Indices, paint);
        surface.Flush();

        _overlay.Present(new PixelRect(origin.X + left, origin.Y + top, width, height));
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

    internal void Clear()
    {
        _overlay.Hide();
        _texture?.Dispose();
        _texture = null;
        _textureCoordinates = null;
    }

    public void Dispose()
    {
        Clear();
        _overlay.Dispose();
    }

    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
