using Avalonia.Media;
using Avalonia.Styling;

namespace PsExporter.Views;

public partial class MainWindow
{
    private void ApplyExportHighlight()
    {
        if (_animationViewModel?.CurrentExportHighlight is not { } highlight) return;
        var resources = ExportContentHost.Resources;
        resources["Brush.Accent"] = ActualThemeVariant == ThemeVariant.Dark ? highlight.DarkInk : highlight.LightInk;
        resources["Brush.SegmentSelected"] = highlight.Tint;
        resources["Brush.SegmentSelectedBorder"] = highlight.Outline;
        resources["Brush.SegmentHover"] = highlight.Hover;
        var fill = ((ISolidColorBrush)highlight.Fill).Color;
        var focusGlow = new BoxShadows(new BoxShadow
        {
            Spread = 2,
            Color = Color.FromArgb(0x60, fill.R, fill.G, fill.B)
        });
        resources["Shadow.Focus"] = focusGlow;
        var buttonResources = StartExportButton.Resources;
        buttonResources["Brush.Accent"] = resources["Brush.Accent"];
        buttonResources["Brush.AccentHover"] = highlight.LightInk;
        buttonResources["Brush.AccentPressed"] = highlight.PressedFill;
        buttonResources["Shadow.Focus"] = focusGlow;
    }
}
