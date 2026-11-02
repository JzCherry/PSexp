using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace PsExporter.Controls;

/// <summary>分段选择轨道。保留原 XAML 类型名；选中状态立即绘制，不播放切换动画。</summary>
public sealed class AnimatedSegmentTrack : ContinuousBorder
{
    public static readonly StyledProperty<object?> SelectionValueProperty =
        AvaloniaProperty.Register<AnimatedSegmentTrack, object?>(nameof(SelectionValue));
    public static readonly AttachedProperty<object?> SelectionAliasProperty =
        AvaloniaProperty.RegisterAttached<AnimatedSegmentTrack, Button, object?>("SelectionAlias");
    public static readonly StyledProperty<IBrush?> IndicatorBrushProperty =
        AvaloniaProperty.Register<AnimatedSegmentTrack, IBrush?>(nameof(IndicatorBrush));
    public static readonly StyledProperty<IBrush?> IndicatorBorderBrushProperty =
        AvaloniaProperty.Register<AnimatedSegmentTrack, IBrush?>(nameof(IndicatorBorderBrush));
    public static readonly StyledProperty<IBrush?> HoverBrushProperty =
        AvaloniaProperty.Register<AnimatedSegmentTrack, IBrush?>(nameof(HoverBrush));

    public object? SelectionValue { get => GetValue(SelectionValueProperty); set => SetValue(SelectionValueProperty, value); }
    public IBrush? IndicatorBrush { get => GetValue(IndicatorBrushProperty); set => SetValue(IndicatorBrushProperty, value); }
    public IBrush? IndicatorBorderBrush { get => GetValue(IndicatorBorderBrushProperty); set => SetValue(IndicatorBorderBrushProperty, value); }
    public IBrush? HoverBrush { get => GetValue(HoverBrushProperty); set => SetValue(HoverBrushProperty, value); }
    public static object? GetSelectionAlias(Button button) => button.GetValue(SelectionAliasProperty);
    public static void SetSelectionAlias(Button button, object? value) => button.SetValue(SelectionAliasProperty, value);

    private readonly ContinuousCornerRenderer _indicatorRenderer = new();
    private readonly ContinuousCornerRenderer _hoverRenderer = new();
    private int _hoverIndex = -1;

    public AnimatedSegmentTrack()
    {
        AddHandler(Button.ClickEvent, OnButtonClick, RoutingStrategies.Bubble);
        PointerExited += (_, _) => { _hoverIndex = -1; InvalidateVisual(); };
    }

    private Button[] Options => Child is Panel panel
        ? panel.Children.OfType<Button>().Where(b => b.IsVisible).ToArray() : [];

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        var index = Array.FindIndex(Options, b => b.IsEffectivelyEnabled
            && b.TranslatePoint(default, this) is { } origin && new Rect(origin, b.Bounds.Size).Contains(point));
        if (_hoverIndex == index) return;
        _hoverIndex = index;
        InvalidateVisual();
    }

    private static bool SameValue(object? left, object? right)
        => left is not null && right is not null
            && string.Equals(Convert.ToString(left, CultureInfo.InvariantCulture),
                Convert.ToString(right, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

    private void OnButtonClick(object? sender, RoutedEventArgs e)
        // Command 可能晚于 Click 路由；只补一次重绘，不排队播放任何动画。
        => Dispatcher.UIThread.Post(InvalidateVisual);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectionValueProperty || change.Property == IndicatorBrushProperty
            || change.Property == IndicatorBorderBrushProperty || change.Property == HoverBrushProperty)
            InvalidateVisual();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        InvalidateVisual();
        return size;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var options = Options;
        if (_hoverIndex >= 0 && _hoverIndex < options.Length)
            DrawOption(context, options[_hoverIndex], _hoverRenderer, HoverBrush, null);
        var selected = Array.Find(options, b => SameValue(SelectionValue, b.CommandParameter)
            || (GetSelectionAlias(b) is { } alias && SameValue(SelectionValue, alias)));
        if (selected is not null)
            DrawOption(context, selected, _indicatorRenderer, IndicatorBrush, IndicatorBorderBrush);
    }

    private void DrawOption(DrawingContext context, Button button, ContinuousCornerRenderer renderer,
        IBrush? fill, IBrush? border)
    {
        if (button.Bounds.Width <= 0 || button.TranslatePoint(default, this) is not { } origin) return;
        using (context.PushTransform(Matrix.CreateTranslation(origin.X, origin.Y)))
            renderer.Render(context, button.Bounds.Size, new CornerRadius(10),
                border is null ? default : new Thickness(1), fill, border);
    }
}
