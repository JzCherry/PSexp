using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace PsExporter.ViewModels;

public sealed class EqualsToBgConverter : IValueConverter
{
    public static readonly EqualsToBgConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return DesignBrushResolver.Get("Brush.SurfaceSubtle");

        string vStr = value.ToString() ?? "";
        string pStr = parameter.ToString() ?? "";

        bool equals = string.Equals(vStr, pStr, StringComparison.OrdinalIgnoreCase);

        return equals ? DesignBrushResolver.Get("Brush.Surface") : DesignBrushResolver.Get("Brush.SurfaceSubtle");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class EqualsToTextBrushConverter : IValueConverter
{
    public static readonly EqualsToTextBrushConverter Instance = new();
    public static readonly EqualsToTextBrushConverter SettingsInstance = new("Brush.OnAccent");
    private readonly string _selectedBrushKey;

    public EqualsToTextBrushConverter() : this("Brush.TextPrimary") { }
    private EqualsToTextBrushConverter(string selectedBrushKey) => _selectedBrushKey = selectedBrushKey;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return DesignBrushResolver.Get("Brush.TextSecondary");

        string vStr = value.ToString() ?? "";
        string pStr = parameter.ToString() ?? "";

        bool equals = string.Equals(vStr, pStr, StringComparison.OrdinalIgnoreCase);

        return equals ? DesignBrushResolver.Get(_selectedBrushKey) : DesignBrushResolver.Get("Brush.TextSecondary");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Returns a filled gradient brush when Quality >= cell threshold, otherwise a light empty brush.
/// Used for the notched quality cell meter design.
/// </summary>
public sealed class QualityCellBrushConverter : IValueConverter
{
    public static readonly QualityCellBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int quality && parameter is string str && int.TryParse(str, out int cellValue))
        {
            return quality >= cellValue
                ? DesignBrushResolver.Get("Brush.Accent")
                : DesignBrushResolver.Get("Brush.Border");
        }

        return DesignBrushResolver.Get("Brush.Border");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
