using Avalonia.Media;
using Avalonia.Styling;

namespace PsExporter.Views;

public partial class MainWindow
{
    private void ApplyExportHighlight()
    {
        if (_animationViewModel?.CurrentExportHighlight is not { } highlight) return;
        var accent = ActualThemeVariant == ThemeVariant.Dark ? highlight.DarkInk : highlight.LightInk;
        var fill = ((ISolidColorBrush)highlight.Fill).Color;
        var focusGlow = new BoxShadows(new BoxShadow
        {
            Spread = 2,
            Color = Color.FromArgb(0x60, fill.R, fill.G, fill.B)
        });
        var settingsResources = SettingsContentHost.Resources;
        foreach (var resources in new[] { ExportContentHost.Resources, settingsResources })
        {
            resources["Brush.Accent"] = accent;
            resources["Brush.SegmentSelected"] = highlight.Tint;
            resources["Brush.SegmentSelectedBorder"] = highlight.Outline;
            resources["Brush.SegmentHover"] = highlight.Hover;
            resources["Shadow.Focus"] = focusGlow;
        }
        settingsResources["Brush.SettingsSelected"] = highlight.Fill;
        settingsResources["Brush.AccentHover"] = highlight.LightInk;
        settingsResources["Brush.AccentPressed"] = highlight.PressedFill;

        var buttonResources = StartExportButton.Resources;
        buttonResources["Brush.Accent"] = accent;
        buttonResources["Brush.AccentHover"] = highlight.LightInk;
        buttonResources["Brush.AccentPressed"] = highlight.PressedFill;
        buttonResources["Shadow.Focus"] = focusGlow;
    }
}
