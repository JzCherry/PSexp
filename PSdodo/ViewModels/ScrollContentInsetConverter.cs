using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace PsExporter.ViewModels;

/// <summary>让自动滚动条使用右侧设计留白，卡片与非滚动区域共用稳定的左右边界。</summary>
public sealed class ScrollContentInsetConverter : IMultiValueConverter
{
    public static readonly ScrollContentInsetConverter Instance = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var inset = parameter is double value && double.IsFinite(value) ? Math.Max(0, value) : 16;
        double gutter = 0;
        // 首次布局或页面隐藏时 Viewport 可能尚未有效，先保留正常对称留白。
        if (values.Count >= 2 && values[0] is double width && values[1] is double viewport
            && double.IsFinite(width) && double.IsFinite(viewport) && width > 0 && viewport > 0)
            gutter = Math.Max(0, width - viewport);
        return new Thickness(inset, 0, Math.Max(0, inset - gutter), 0);
    }
}
