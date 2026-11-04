using Avalonia;

namespace PsExporter.Services;

/// <summary>
/// 跟随鼠标唤起的落位算法。
/// 入参与返回值都用物理像素，和 Window.Position 同单位；窗口尺寸由调用方按该屏缩放比换算后传入。
/// </summary>
public static class SummonPlacement
{
    /// <summary>窗口与光标之间留的空隙，按 DIP 记，落位时再乘缩放比。</summary>
    public const double GapDip = 16;

    /// <summary>默认放在光标右下方；放不下就翻到另一侧，最后仍钳进工作区，保证窗口整块可见。</summary>
    public static PixelPoint Beside(PixelPoint cursor, int width, int height, PixelRect workArea, int gap)
    {
        var x = cursor.X + gap;
        if (x + width > workArea.Right && cursor.X - gap - width >= workArea.X)
            x = cursor.X - gap - width;
        var y = cursor.Y + gap;
        if (y + height > workArea.Bottom && cursor.Y - gap - height >= workArea.Y)
            y = cursor.Y - gap - height;
        return ClampToWorkArea(new PixelPoint(x, y), width, height, workArea);
    }

    public static PixelPoint Centered(PixelPoint cursor, int width, int height, PixelRect workArea)
        => ClampToWorkArea(new PixelPoint(cursor.X - width / 2, cursor.Y - height / 2), width, height, workArea);

    private static PixelPoint ClampToWorkArea(PixelPoint position, int width, int height, PixelRect workArea)
        => new(
            Math.Clamp(position.X, workArea.X, Math.Max(workArea.X, workArea.Right - width)),
            Math.Clamp(position.Y, workArea.Y, Math.Max(workArea.Y, workArea.Bottom - height)));
}
