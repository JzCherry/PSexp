using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using PsExporter.Services;
using SkiaSharp;

namespace PsExporter.Controls;

// 每张纹理只创建一次 image/shader/paint；帧操作持有引用，防止 UI 交接时释放渲染线程仍在使用的资源。
internal sealed class GenieTexture : IDisposable
{
    private int _references = 1;
    private readonly SKImage _image;
    private readonly SKShader _shader;
    internal SKPaint Paint { get; }
    internal GenieTexture(SKBitmap bitmap)
    {
        _image = SKImage.FromBitmap(bitmap);
        _shader = _image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        Paint = new SKPaint { Shader = _shader, IsAntialias = true };
    }
    internal GenieTexture Acquire() { System.Threading.Interlocked.Increment(ref _references); return this; }
    public void Dispose()
    {
        if (System.Threading.Interlocked.Decrement(ref _references) != 0) return;
        Paint.Dispose(); _shader.Dispose(); _image.Dispose();
    }
}

// 仅收集实际进入 Skia 绘制的间隔；它不是显示器最终呈现帧率。
internal sealed class GenieFrameTiming
{
    private long _last;
    private long _maxGap;
    private int _frames;

    internal void Drawn()
    {
        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Exchange(ref _last, now);
        Interlocked.Increment(ref _frames);
        if (previous == 0) return;
        var gap = now - previous;
        long old;
        do { old = Volatile.Read(ref _maxGap); if (gap <= old) return; }
        while (Interlocked.CompareExchange(ref _maxGap, gap, old) != old);
    }

    internal string Summary => FormattableString.Invariant(
        $"draw_frames={Volatile.Read(ref _frames)} draw_max_gap_ms={Volatile.Read(ref _maxGap) * 1000d / Stopwatch.Frequency:F1}");
}

internal class GenieMeshOperation : ICustomDrawOperation
{
    public Rect Bounds { get; }
    private GenieTexture? _texture;
    public EdgeSnapHelper.GenieMeshData? MeshData { get; }
    public SKPoint[]? TextureCoordinates { get; }
    private readonly byte _alpha;
    private readonly TaskCompletionSource<bool>? _drawReady;
    private readonly GenieFrameTiming _timing;

    public GenieMeshOperation(
        Rect bounds,
        GenieTexture texture,
        EdgeSnapHelper.GenieMeshData? meshData,
        SKPoint[]? textureCoordinates,
        double opacity,
        TaskCompletionSource<bool>? drawReady,
        GenieFrameTiming timing)
    {
        Bounds = bounds;
        _texture = texture.Acquire();
        MeshData = meshData;
        TextureCoordinates = textureCoordinates;
        _alpha = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        _drawReady = drawReady;
        _timing = timing;
    }

    public void Dispose() => System.Threading.Interlocked.Exchange(ref _texture, null)?.Dispose();

    public bool Equals(ICustomDrawOperation? other) => false;

    public bool HitTest(Point p) => false;

    public void Render(ImmediateDrawingContext context)
    {
        var skiaContext = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (skiaContext is null || _texture is null || MeshData is null || TextureCoordinates is null) return;

        using (var lease = skiaContext.Lease())
        {
            var canvas = lease.SkCanvas;
            // 直接在 Skia 画笔上改变透明度，避免为整个动画表面额外建立 opacity 图层。
            // Paint 只在渲染线程修改；每个绘制操作都保存自己对应的透明度。
            _texture.Paint.Color = SKColors.White.WithAlpha(_alpha);
            canvas.DrawVertices(
                SKVertexMode.Triangles,
                MeshData.Vertices,
                TextureCoordinates,
                null,
                MeshData.Indices,
                _texture.Paint);
        }
        _timing.Drawn();
        _drawReady?.TrySetResult(true);
    }
}

public class GenieMeshControl : Control
{
    private SKPoint[]? _cachedTextureCoordinates;
    private SKPoint[]? _textureCoordinatesSource;
    private GenieTexture? _texture;
    private double _cachedTextureScaleX = double.NaN;
    private double _cachedTextureScaleY = double.NaN;
    private TaskCompletionSource<bool>? _drawReady;
    private GenieFrameTiming _frameTiming = new();

    internal string FrameTimingSummary => _frameTiming.Summary;
    internal void BeginFrameTiming() => _frameTiming = new GenieFrameTiming();

    internal Task<bool> WaitForDrawAsync()
    {
        CancelPendingDraw();
        _drawReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        InvalidateVisual();
        return _drawReady.Task;
    }

    internal void CancelPendingDraw()
    {
        _drawReady?.TrySetResult(false);
        _drawReady = null;
    }

    public static readonly StyledProperty<SKBitmap?> BitmapProperty =
        AvaloniaProperty.Register<GenieMeshControl, SKBitmap?>(nameof(Bitmap));

    public static readonly StyledProperty<EdgeSnapHelper.GenieMeshData?> MeshDataProperty =
        AvaloniaProperty.Register<GenieMeshControl, EdgeSnapHelper.GenieMeshData?>(nameof(MeshData));

    public static readonly StyledProperty<double> TextureScaleProperty =
        AvaloniaProperty.Register<GenieMeshControl, double>(nameof(TextureScale), 1.0);

    public static readonly StyledProperty<double> MeshOpacityProperty =
        AvaloniaProperty.Register<GenieMeshControl, double>(nameof(MeshOpacity), 1);

    public double MeshOpacity
    {
        get => GetValue(MeshOpacityProperty);
        set => SetValue(MeshOpacityProperty, value);
    }

    public SKBitmap? Bitmap
    {
        get => GetValue(BitmapProperty);
        set => SetValue(BitmapProperty, value);
    }

    public EdgeSnapHelper.GenieMeshData? MeshData
    {
        get => GetValue(MeshDataProperty);
        set => SetValue(MeshDataProperty, value);
    }

    public double TextureScale
    {
        get => GetValue(TextureScaleProperty);
        set => SetValue(TextureScaleProperty, value);
    }

    static GenieMeshControl()
    {
        AffectsRender<GenieMeshControl>(BitmapProperty, MeshDataProperty, TextureScaleProperty, MeshOpacityProperty);
    }

    private SKPoint[]? GetTextureCoordinates()
    {
        if (MeshData is null || Bitmap is null || MeshData.SourceWidth <= 0 || MeshData.SourceHeight <= 0) return null;
        // 每个方向独立按实际纹理像素映射。125%/175% 等缩放会产生取整，
        // 不能把名义 DPI 当作两个方向都绝对精确的纹理比例。
        var scaleX = Bitmap.Width / (double)MeshData.SourceWidth;
        var scaleY = Bitmap.Height / (double)MeshData.SourceHeight;
        if (_cachedTextureCoordinates is not null
            && _cachedTextureScaleX == scaleX && _cachedTextureScaleY == scaleY
            && ReferenceEquals(_textureCoordinatesSource, MeshData.TexCoords))
            return _cachedTextureCoordinates;

        _textureCoordinatesSource = MeshData.TexCoords;
        _cachedTextureScaleX = scaleX;
        _cachedTextureScaleY = scaleY;
        _cachedTextureCoordinates = new SKPoint[MeshData.TexCoords.Length];
        for (var index = 0; index < _cachedTextureCoordinates.Length; index++)
        {
            var point = MeshData.TexCoords[index];
            _cachedTextureCoordinates[index] = new SKPoint(
                (float)(point.X * scaleX),
                (float)(point.Y * scaleY));
        }
        return _cachedTextureCoordinates;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BitmapProperty)
        {
            var previous = _texture;
            _texture = Bitmap is null ? null : new GenieTexture(Bitmap);
            previous?.Dispose();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_texture is null || MeshData is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        context.Custom(new GenieMeshOperation(
            new Rect(0, 0, Bounds.Width, Bounds.Height),
            _texture,
            MeshData,
            GetTextureCoordinates(),
            MeshOpacity,
            _drawReady is { Task.IsCompleted: false } ? _drawReady : null,
            _frameTiming));
    }
}
