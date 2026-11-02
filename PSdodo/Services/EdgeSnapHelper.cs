using System;
using System.Collections.Generic;
using Avalonia;
using SkiaSharp;

namespace PsExporter.Services;

public enum DockEdge
{
    None,
    Left,
    Right,
    Top,
    Bottom
}

public readonly record struct SnapResult(
    bool IsSnapped,
    PixelPoint TargetPosition,
    bool SnappedLeft,
    bool SnappedRight,
    bool SnappedTop,
    bool SnappedBottom);

public readonly record struct DockTargetResult(
    bool IsDockable,
    DockEdge Edge,
    PixelPoint ExpandedPosition,
    PixelPoint HiddenPosition);

/// <summary>收纳后固定在屏幕边缘的图标触发器位置（物理像素）。</summary>
public readonly record struct DockIconPlacement(
    PixelPoint Position,
    int SizePx);

/// <summary>
/// 窗口贴边吸附、边缘收纳和 Q 弹阻尼动画计算辅助类。
/// 坐标统一使用物理像素，与 Avalonia Window.Position 和 Win32 光标坐标一致。
/// </summary>
public static class EdgeSnapHelper
{
    /// <summary>
    /// 根据窗口位置尺寸和屏幕工作区，计算贴边吸附目标位置。
    /// 保留该方法用于兼容已有测试和调用方。
    /// </summary>
    public static SnapResult CalculateSnapTarget(PixelRect windowRect, PixelRect workingArea, int thresholdPx = 24)
    {
        var targetX = windowRect.X;
        var targetY = windowRect.Y;
        var snappedLeft = false;
        var snappedRight = false;
        var snappedTop = false;
        var snappedBottom = false;

        var distLeft = Math.Abs(windowRect.X - workingArea.X);
        if (distLeft <= thresholdPx)
        {
            targetX = workingArea.X;
            snappedLeft = true;
        }

        if (!snappedLeft)
        {
            var windowRight = windowRect.X + windowRect.Width;
            var workRight = workingArea.X + workingArea.Width;
            var distRight = Math.Abs(windowRight - workRight);
            if (distRight <= thresholdPx)
            {
                targetX = workRight - windowRect.Width;
                snappedRight = true;
            }
        }

        var distTop = Math.Abs(windowRect.Y - workingArea.Y);
        if (distTop <= thresholdPx)
        {
            targetY = workingArea.Y;
            snappedTop = true;
        }

        if (!snappedTop)
        {
            var windowBottom = windowRect.Y + windowRect.Height;
            var workBottom = workingArea.Y + workingArea.Height;
            var distBottom = Math.Abs(windowBottom - workBottom);
            if (distBottom <= thresholdPx)
            {
                targetY = workBottom - windowRect.Height;
                snappedBottom = true;
            }
        }

        var isSnapped = snappedLeft || snappedRight || snappedTop || snappedBottom;
        var targetPos = isSnapped
            ? new PixelPoint(targetX, targetY)
            : new PixelPoint(windowRect.X, windowRect.Y);

        return new SnapResult(
            isSnapped,
            targetPos,
            snappedLeft,
            snappedRight,
            snappedTop,
            snappedBottom);
    }

    /// <summary>
    /// 计算真正的边缘收纳目标：窗口在边缘内侧阈值范围内，或已经部分穿过屏幕外沿时均可触发。
    /// 只要窗口仍与当前工作区有交叠（没有完全移出），即使已有一半窗口在屏幕外也能正常收纳。
    /// </summary>
    public static DockTargetResult CalculateDockTarget(
        PixelRect windowRect,
        PixelRect workingArea,
        int thresholdPx = 48,
        int peekPx = 12)
    {
        var threshold = Math.Max(1, thresholdPx);
        var safePeek = Math.Clamp(peekPx, 0, Math.Max(0, Math.Min(windowRect.Width, windowRect.Height) - 1));
        var verticalOverlap = Math.Min(windowRect.Bottom, workingArea.Bottom) - Math.Max(windowRect.Y, workingArea.Y);
        var horizontalOverlap = Math.Min(windowRect.Right, workingArea.Right) - Math.Max(windowRect.X, workingArea.X);

        var candidates = new (DockEdge Edge, bool Eligible, int Distance, PixelPoint Expanded)[]
        {
            (DockEdge.Left,
                verticalOverlap > 0 && windowRect.Right > workingArea.X && windowRect.X <= workingArea.X + threshold,
                windowRect.X <= workingArea.X ? 0 : windowRect.X - workingArea.X,
                new PixelPoint(workingArea.X, ClampPerpendicular(windowRect.Y, workingArea.Y, workingArea.Height, windowRect.Height))),
            (DockEdge.Right,
                verticalOverlap > 0 && windowRect.X < workingArea.Right && windowRect.Right >= workingArea.Right - threshold,
                windowRect.Right >= workingArea.Right ? 0 : workingArea.Right - windowRect.Right,
                new PixelPoint(workingArea.Right - windowRect.Width, ClampPerpendicular(windowRect.Y, workingArea.Y, workingArea.Height, windowRect.Height))),
            (DockEdge.Top,
                horizontalOverlap > 0 && windowRect.Bottom > workingArea.Y && windowRect.Y <= workingArea.Y + threshold,
                windowRect.Y <= workingArea.Y ? 0 : windowRect.Y - workingArea.Y,
                new PixelPoint(ClampPerpendicular(windowRect.X, workingArea.X, workingArea.Width, windowRect.Width), workingArea.Y)),
            (DockEdge.Bottom,
                horizontalOverlap > 0 && windowRect.Y < workingArea.Bottom && windowRect.Bottom >= workingArea.Bottom - threshold,
                windowRect.Bottom >= workingArea.Bottom ? 0 : workingArea.Bottom - windowRect.Bottom,
                new PixelPoint(ClampPerpendicular(windowRect.X, workingArea.X, workingArea.Width, windowRect.Width), workingArea.Bottom - windowRect.Height))
        };

        var found = false;
        var candidate = candidates[0];
        foreach (var current in candidates)
        {
            if (!current.Eligible) continue;
            if (!found || current.Distance < candidate.Distance)
            {
                candidate = current;
                found = true;
            }
        }

        if (!found)
            return new DockTargetResult(false, DockEdge.None, new PixelPoint(windowRect.X, windowRect.Y), new PixelPoint(windowRect.X, windowRect.Y));

        var hidden = candidate.Edge switch
        {
            DockEdge.Left => new PixelPoint(workingArea.X - windowRect.Width + safePeek, candidate.Expanded.Y),
            DockEdge.Right => new PixelPoint(workingArea.Right - safePeek, candidate.Expanded.Y),
            DockEdge.Top => new PixelPoint(candidate.Expanded.X, workingArea.Y - windowRect.Height + safePeek),
            DockEdge.Bottom => new PixelPoint(candidate.Expanded.X, workingArea.Bottom - safePeek),
            _ => candidate.Expanded
        };

        return new DockTargetResult(true, candidate.Edge, candidate.Expanded, hidden);
    }

    /// <summary>判断屏幕某条边外侧是否没有相邻显示器；共享边作为跨屏通道，不自动收纳。</summary>
    public static bool IsOuterScreenEdge(PixelRect screenBounds, IReadOnlyList<PixelRect> allScreenBounds, DockEdge edge)
    {
        foreach (var other in allScreenBounds)
        {
            if (other == screenBounds) continue;
            var verticalOverlap = Math.Min(screenBounds.Bottom, other.Bottom) - Math.Max(screenBounds.Y, other.Y);
            var horizontalOverlap = Math.Min(screenBounds.Right, other.Right) - Math.Max(screenBounds.X, other.X);
            var isAdjacent = edge switch
            {
                DockEdge.Left => verticalOverlap > 0 && Math.Abs(other.Right - screenBounds.X) <= 1,
                DockEdge.Right => verticalOverlap > 0 && Math.Abs(other.X - screenBounds.Right) <= 1,
                DockEdge.Top => horizontalOverlap > 0 && Math.Abs(other.Bottom - screenBounds.Y) <= 1,
                DockEdge.Bottom => horizontalOverlap > 0 && Math.Abs(other.Y - screenBounds.Bottom) <= 1,
                _ => false
            };
            if (isAdjacent) return false;
        }
        return edge != DockEdge.None;
    }

    /// <summary>
    /// 根据隐藏后的完整窗口矩形计算精确的鼠标感应区。
    /// 感应区只覆盖窗口露出的窄条及其附近，不再把整条屏幕边缘都当作触发区。
    /// </summary>
    public static PixelRect CalculateDockHotZone(
        PixelRect hiddenRect,
        PixelRect workingArea,
        DockEdge edge,
        int inwardPaddingPx = 24,
        int perpendicularPaddingPx = 14)
    {
        var visibleLeft = Math.Max(hiddenRect.X, workingArea.X);
        var visibleTop = Math.Max(hiddenRect.Y, workingArea.Y);
        var visibleRight = Math.Min(hiddenRect.Right, workingArea.Right);
        var visibleBottom = Math.Min(hiddenRect.Bottom, workingArea.Bottom);
        if (visibleRight <= visibleLeft || visibleBottom <= visibleTop)
            return new PixelRect(workingArea.X, workingArea.Y, 0, 0);

        var zoneLeft = visibleLeft;
        var zoneTop = visibleTop;
        var zoneRight = visibleRight;
        var zoneBottom = visibleBottom;

        switch (edge)
        {
            case DockEdge.Left:
                zoneRight = visibleRight + Math.Max(0, inwardPaddingPx);
                zoneTop -= Math.Max(0, perpendicularPaddingPx);
                zoneBottom += Math.Max(0, perpendicularPaddingPx);
                break;
            case DockEdge.Right:
                zoneLeft = visibleLeft - Math.Max(0, inwardPaddingPx);
                zoneTop -= Math.Max(0, perpendicularPaddingPx);
                zoneBottom += Math.Max(0, perpendicularPaddingPx);
                break;
            case DockEdge.Top:
                zoneBottom = visibleBottom + Math.Max(0, inwardPaddingPx);
                zoneLeft -= Math.Max(0, perpendicularPaddingPx);
                zoneRight += Math.Max(0, perpendicularPaddingPx);
                break;
            case DockEdge.Bottom:
                zoneTop = visibleTop - Math.Max(0, inwardPaddingPx);
                zoneLeft -= Math.Max(0, perpendicularPaddingPx);
                zoneRight += Math.Max(0, perpendicularPaddingPx);
                break;
        }

        zoneLeft = Math.Max(zoneLeft, workingArea.X);
        zoneTop = Math.Max(zoneTop, workingArea.Y);
        zoneRight = Math.Min(zoneRight, workingArea.Right);
        zoneBottom = Math.Min(zoneBottom, workingArea.Bottom);
        return zoneRight <= zoneLeft || zoneBottom <= zoneTop
            ? new PixelRect(workingArea.X, workingArea.Y, 0, 0)
            : new PixelRect(zoneLeft, zoneTop, zoneRight - zoneLeft, zoneBottom - zoneTop);
    }

    public static bool IsPointInRect(PixelRect rect, int x, int y)
        => x >= rect.X && x < rect.Right && y >= rect.Y && y < rect.Bottom;

    /// <summary>
    /// 计算收纳图标的位置：图标只露出一半作为贴边触发器，并沿原窗口的中轴对齐。
    /// 图标取代完整窗口作为唯一收纳态入口，避免隐藏窗口或窄条继续出现在屏幕边缘。
    /// </summary>
    public static DockIconPlacement CalculateDockIconPlacement(
        PixelRect workingArea,
        PixelPoint expandedPosition,
        int windowWidthPx,
        int windowHeightPx,
        DockEdge edge,
        int iconSizePx = 52,
        int edgeInsetPx = 0)
    {
        var size = Math.Clamp(iconSizePx, 32, 96);
        var visibleInset = Math.Clamp(edgeInsetPx, 0, size);
        var hiddenOffset = Math.Max(0, size - visibleInset);
        var centerX = expandedPosition.X + Math.Max(0, windowWidthPx - size) / 2;
        var centerY = expandedPosition.Y + Math.Max(0, windowHeightPx - size) / 2;
        var minVisibleX = workingArea.X - hiddenOffset;
        var maxVisibleX = workingArea.Right - visibleInset;
        var minVisibleY = workingArea.Y - hiddenOffset;
        var maxVisibleY = workingArea.Bottom - visibleInset;

        var x = edge switch
        {
            DockEdge.Left => workingArea.X - hiddenOffset,
            DockEdge.Right => workingArea.Right - visibleInset,
            _ => Math.Clamp(centerX, minVisibleX, maxVisibleX)
        };
        var y = edge switch
        {
            DockEdge.Top => workingArea.Y - hiddenOffset,
            DockEdge.Bottom => workingArea.Bottom - visibleInset,
            _ => Math.Clamp(centerY, minVisibleY, maxVisibleY)
        };

        return new DockIconPlacement(new PixelPoint(x, y), size);
    }

    private static int ClampPerpendicular(int value, int areaStart, int areaSize, int windowSize)
    {
        var max = areaStart + Math.Max(0, areaSize - windowSize);
        return Math.Clamp(value, areaStart, max);
    }

    /// <summary>
    /// 计算 Q 弹阻尼动画缓动值。
    /// 使用 macOS 风格流体阻尼弹簧模型：丝滑起步、约 4.6% 过冲、慢节奏流体收尾，极度平滑。
    /// </summary>
    public static double CalculateElasticBounceProgress(double progress)
        => CalculateDampedSpringProgress(progress, dampingRatio: 0.70, naturalFrequency: 11.5);

    public static double CalculateDampedSpringProgress(double progress, double dampingRatio, double naturalFrequency)
    {
        var t = Math.Clamp(progress, 0.0, 1.0);
        if (t <= 0) return 0.0;
        if (t >= 1.0) return 1.0;

        var zeta = Math.Clamp(dampingRatio, 0.55, 0.95);
        var omega = Math.Clamp(naturalFrequency, 6.0, 24.0);
        var dampedFrequency = omega * Math.Sqrt(1.0 - zeta * zeta);
        var phase = dampedFrequency * t;
        var decay = Math.Exp(-zeta * omega * t);
        var response = decay * (Math.Cos(phase) + zeta * omega / dampedFrequency * Math.Sin(phase));
        return 1.0 - response;
    }

    /// <summary>
    /// 神灯网格使用严格单调的 smootherstep 时间曲线。
    /// 弹簧过冲会被网格的 0..1 收纳程度截断，造成提前停到终点后再回摆的卡顿感；
    /// 此曲线保持零起止速度和连续加速度，让每一帧都沿同一方向稳定推进。
    /// </summary>
    public static double CalculateGenieAnimationProgress(double progress)
    {
        var t = Math.Clamp(progress, 0.0, 1.0);
        return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
    }

    public readonly record struct DockTransformFrame(
        double ScaleX,
        double ScaleY,
        RelativePoint Origin);

    public readonly record struct GenieWarpFrame(
        double ScaleX,
        double ScaleY,
        double SkewX,
        double SkewY,
        RelativePoint Origin);

    /// <summary>
    /// 计算 macOS 经典神灯漏斗吸入/吐出 (Genie Warp) 形变帧。
    /// 贴边侧先急剧收缩成狭窄瓶口，远离侧保持宽度并产生斜向切变 (Skew)，
    /// 打造前窄后宽漏斗状拉扯吸入感。
    /// </summary>
    public static GenieWarpFrame CalculateGenieWarpTransform(
        DockEdge edge,
        double progress,
        bool isHiding)
    {
        if (edge == DockEdge.None)
            return new GenieWarpFrame(1.0, 1.0, 0.0, 0.0, RelativePoint.Center);

        var p = Math.Clamp(progress, 0.0, 1.2);
        var factor = isHiding ? Math.Min(1.0, p) : Math.Max(0.0, 1.0 - p);

        // 1. 漏斗瓶口主轴与垂直轴收窄 (Funnel Squeeze)
        var funnelCurve = Math.Sin(factor * Math.PI);
        var mainScale = 1.0 - 0.38 * factor;
        var crossScale = 1.0 - 0.45 * factor - 0.06 * funnelCurve;

        // 2. 神灯斜向剪切/甩尾感 (Genie Skew)
        var skewAngle = funnelCurve * 9.5;

        return edge switch
        {
            DockEdge.Left => new GenieWarpFrame(
                mainScale,
                crossScale,
                0.0,
                isHiding ? -skewAngle : skewAngle,
                new RelativePoint(0.0, 0.5, RelativeUnit.Relative)),
            DockEdge.Right => new GenieWarpFrame(
                mainScale,
                crossScale,
                0.0,
                isHiding ? skewAngle : -skewAngle,
                new RelativePoint(1.0, 0.5, RelativeUnit.Relative)),
            DockEdge.Top => new GenieWarpFrame(
                crossScale,
                mainScale,
                isHiding ? -skewAngle : skewAngle,
                0.0,
                new RelativePoint(0.5, 0.0, RelativeUnit.Relative)),
            DockEdge.Bottom => new GenieWarpFrame(
                crossScale,
                mainScale,
                isHiding ? skewAngle : -skewAngle,
                0.0,
                new RelativePoint(0.5, 1.0, RelativeUnit.Relative)),
            _ => new GenieWarpFrame(1.0, 1.0, 0.0, 0.0, RelativePoint.Center)
        };
    }

    /// <summary>
    /// 计算 Mac Dock 样式的收窄与释放变形帧。
    /// 收纳滑入时向边缘方向“收窄挤压”（Scale 1.0 -> minScale），
    /// 触碰展开滑出时像橡胶弹簧一样“释放扩展”（Scale minScale -> 1.04 -> 1.0）。
    /// </summary>
    public static DockTransformFrame CalculateDockSqueezeTransform(
        DockEdge edge,
        double easeProgress,
        bool isHiding,
        double minScale = 0.78)
    {
        if (edge == DockEdge.None)
            return new DockTransformFrame(1.0, 1.0, RelativePoint.Center);

        var clampedMin = Math.Clamp(minScale, 0.5, 0.95);
        var progress = Math.Clamp(easeProgress, 0.0, 1.2);
        var mainScale = isHiding
            ? 1.0 - (1.0 - clampedMin) * Math.Min(1.0, progress)
            : clampedMin + (1.0 - clampedMin) * progress;

        var perpScale = isHiding
            ? 1.0 - 0.05 * Math.Min(1.0, progress)
            : 0.95 + 0.05 * Math.Min(1.0, progress);

        return edge switch
        {
            DockEdge.Left => new DockTransformFrame(mainScale, perpScale, new RelativePoint(0.0, 0.5, RelativeUnit.Relative)),
            DockEdge.Right => new DockTransformFrame(mainScale, perpScale, new RelativePoint(1.0, 0.5, RelativeUnit.Relative)),
            DockEdge.Top => new DockTransformFrame(perpScale, mainScale, new RelativePoint(0.5, 0.0, RelativeUnit.Relative)),
            DockEdge.Bottom => new DockTransformFrame(perpScale, mainScale, new RelativePoint(0.5, 1.0, RelativeUnit.Relative)),
            _ => new DockTransformFrame(1.0, 1.0, RelativePoint.Center)
        };
    }

    public static PixelPoint CalculateInterpolatedPosition(PixelPoint start, PixelPoint target, double easedProgress)
    {
        var x = (int)Math.Round(start.X + (target.X - start.X) * easedProgress);
        var y = (int)Math.Round(start.Y + (target.Y - start.Y) * easedProgress);
        return new PixelPoint(x, y);
    }

    public sealed class GenieMeshData
    {
        public float SourceWidth { get; init; }
        public float SourceHeight { get; init; }
        public int Columns { get; init; }
        public int Rows { get; init; }
        public required SKPoint[] Vertices { get; init; }
        public required SKPoint[] TexCoords { get; init; }
        public required ushort[] Indices { get; init; }
    }

    /// <summary>
    /// 二维纹理网格神灯变形：靠近边缘的顶点先收缩，远端延迟跟随。
    /// dockAnchor / dockSpan 为动画承载窗中的 DIP 坐标；省略时保留边缘狭缝端点。
    /// </summary>
    public static GenieMeshData CalculateGenieMeshData(
        DockEdge edge,
        double progress,
        bool isHiding,
        float width,
        float height,
        int cols = 16,
        int rows = 16,
        float peekPx = 12f,
        GenieMeshData? topology = null,
        SKPoint? dockAnchor = null,
        float? dockSpan = null)
    {
        var clampedCols = Math.Clamp(cols, 4, 32);
        var clampedRows = Math.Clamp(rows, 4, 32);
        var numVertices = (clampedCols + 1) * (clampedRows + 1);
        var numTriangles = clampedCols * clampedRows * 2;

        var vertices = new SKPoint[numVertices];
        var reuseTopology = topology is not null && topology.SourceWidth == width && topology.SourceHeight == height
            && topology.Columns == clampedCols && topology.Rows == clampedRows;
        var texCoords = reuseTopology ? topology!.TexCoords : new SKPoint[numVertices];
        var indices = reuseTopology ? topology!.Indices : new ushort[numTriangles * 3];

        var pRaw = Math.Clamp(progress, 0.0, 1.0);
        // 网格函数只接收“当前收纳程度”：0=完整窗口，1=完全进入狭缝。
        // 展开动画由调用方把时间进度反向映射，避免隐藏/展开端点语义混乱。
        var p = isHiding ? pRaw : 1.0 - pRaw;

        // 建立顶点坐标与纹理坐标映射
        for (var r = 0; r <= clampedRows; r++)
        {
            var v = (float)r / clampedRows;
            var origY = v * height;

            for (var c = 0; c <= clampedCols; c++)
            {
                var u = (float)c / clampedCols;
                var origX = u * width;
                var index = r * (clampedCols + 1) + c;

                if (!reuseTopology) texCoords[index] = new SKPoint(origX, origY);

                if (edge == DockEdge.None || p <= 0.0001)
                {
                    vertices[index] = new SKPoint(origX, origY);
                    continue;
                }

                var vertical = edge is DockEdge.Left or DockEdge.Right;
                var distance = edge switch
                {
                    DockEdge.Left => u,
                    DockEdge.Right => 1f - u,
                    DockEdge.Top => v,
                    DockEdge.Bottom => 1f - v,
                    _ => 0f
                };
                // 连续的空间延迟：每一列都移动到真正端点才停，避免展开后半段
                // 大部分网格提前固定、只剩窄条运动，末尾看起来像突然换了一张图。
                var local = (float)Math.Pow(p, 1 + distance * 1.35);
                var anchor = dockAnchor ?? (edge switch
                {
                    DockEdge.Left => new SKPoint(peekPx, height / 2),
                    DockEdge.Right => new SKPoint(width - peekPx, height / 2),
                    DockEdge.Top => new SKPoint(width / 2, peekPx),
                    _ => new SKPoint(width / 2, height - peekPx)
                });
                var crossSize = vertical ? height : width;
                var span = Math.Clamp(dockSpan ?? crossSize * 0.12f, 0, crossSize);
                // 脊线只在中途弯曲；终点必须准确落在同一个图标中心，不能留下弯曲残影。
                var spine = (float)(Math.Sin(distance * Math.PI) * local * (1 - local) * crossSize * 0.22);
                var targetX = vertical ? anchor.X : anchor.X + (u - 0.5f) * span;
                var targetY = vertical ? anchor.Y + (v - 0.5f) * span : anchor.Y;
                var xPos = origX + (targetX - origX) * local + (vertical ? 0 : spine);
                var yPos = origY + (targetY - origY) * local + (vertical ? spine : 0);

                vertices[index] = new SKPoint(xPos, yPos);
            }
        }

        // 构建网格三角形索引 (Triangulation)
        var idxIndex = 0;
        for (var r = 0; !reuseTopology && r < clampedRows; r++)
        {
            for (var c = 0; c < clampedCols; c++)
            {
                var topLeft = (ushort)(r * (clampedCols + 1) + c);
                var topRight = (ushort)(topLeft + 1);
                var bottomLeft = (ushort)((r + 1) * (clampedCols + 1) + c);
                var bottomRight = (ushort)(bottomLeft + 1);

                // 三角形 1
                indices[idxIndex++] = topLeft;
                indices[idxIndex++] = topRight;
                indices[idxIndex++] = bottomLeft;

                // 三角形 2
                indices[idxIndex++] = topRight;
                indices[idxIndex++] = bottomRight;
                indices[idxIndex++] = bottomLeft;
            }
        }

        return new GenieMeshData
        {
            SourceWidth = width, SourceHeight = height, Columns = clampedCols, Rows = clampedRows,
            Vertices = vertices,
            TexCoords = texCoords,
            Indices = indices
        };
    }
}
