using Avalonia;
using Avalonia.Controls;

namespace PsExporter.Controls;

/// <summary>按选项内容测量的单行分段布局；剩余空间均分，按钮之间保留独立间距。</summary>
public sealed class SegmentPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<SegmentPanel, double>(nameof(Spacing), 4);
    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    static SegmentPanel() => AffectsMeasure<SegmentPanel>(SpacingProperty);

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = 0, height = 0;
        var count = 0;
        foreach (var child in Children.Where(c => c.IsVisible))
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            width += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
            count++;
        }
        return new Size(width + Math.Max(0, count - 1) * Math.Max(0, Spacing), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = Children.Where(c => c.IsVisible).ToArray();
        if (children.Length == 0) return finalSize;
        var gap = Math.Max(0, Spacing);
        var contentWidth = children.Sum(c => c.DesiredSize.Width) + (children.Length - 1) * gap;
        var extra = Math.Max(0, finalSize.Width - contentWidth) / children.Length;
        double x = 0;
        foreach (var child in children)
        {
            var width = child.DesiredSize.Width + extra;
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width + gap;
        }
        return finalSize;
    }
}

/// <summary>宽度足够时使用标签列；窄窗将标签移至上方，整组分段仍保持单行。</summary>
public sealed class SegmentOptionRow : Panel
{
    public static readonly StyledProperty<double> LabelWidthProperty =
        AvaloniaProperty.Register<SegmentOptionRow, double>(nameof(LabelWidth), 76);
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<SegmentOptionRow, double>(nameof(Spacing), 12);
    public double LabelWidth { get => GetValue(LabelWidthProperty); set => SetValue(LabelWidthProperty, value); }
    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    private bool _stacked;

    static SegmentOptionRow() => AffectsMeasure<SegmentOptionRow>(LabelWidthProperty, SpacingProperty);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count != 2) return default;
        var label = Children[0];
        var options = Children[1];
        options.Measure(Size.Infinity);
        _stacked = double.IsFinite(availableSize.Width)
            && LabelWidth + Spacing + options.DesiredSize.Width > availableSize.Width;
        if (_stacked)
        {
            label.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            options.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            return new Size(Math.Max(label.DesiredSize.Width, options.DesiredSize.Width),
                label.DesiredSize.Height + 6 + options.DesiredSize.Height);
        }
        label.Measure(new Size(LabelWidth, double.PositiveInfinity));
        options.Measure(new Size(Math.Max(0, availableSize.Width - LabelWidth - Spacing), double.PositiveInfinity));
        return new Size(LabelWidth + Spacing + options.DesiredSize.Width,
            Math.Max(label.DesiredSize.Height, options.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count != 2) return finalSize;
        var label = Children[0];
        var options = Children[1];
        if (_stacked)
        {
            var labelHeight = label.DesiredSize.Height;
            label.Arrange(new Rect(0, 0, finalSize.Width, labelHeight));
            options.Arrange(new Rect(0, labelHeight + 6, finalSize.Width, Math.Max(0, finalSize.Height - labelHeight - 6)));
        }
        else
        {
            label.Arrange(new Rect(0, 0, LabelWidth, finalSize.Height));
            options.Arrange(new Rect(LabelWidth + Spacing, 0, Math.Max(0, finalSize.Width - LabelWidth - Spacing), finalSize.Height));
        }
        return finalSize;
    }
}
