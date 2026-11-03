using Avalonia.Controls;
using Avalonia.Input;
using PsExporter.Models;
using PsExporter.ViewModels;

namespace PsExporter.Views;

public partial class MainWindow
{
    private bool _exportEnterHeld;

    private void OnExportShortcutKeyDown(object? sender, KeyEventArgs args)
    {
        var isEnter = args.Key == Key.Enter && args.KeyModifiers == KeyModifiers.None;
        var mode = args.KeyModifiers == KeyModifiers.Shift ? args.Key switch
        {
            Key.D1 => ExportMode.QuickExport,
            Key.D2 => ExportMode.AllArtboards,
            Key.D3 => ExportMode.Layers,
            Key.D4 => ExportMode.Slices,
            _ => (ExportMode?)null
        } : null;
        if ((!isEnter && mode is null) || args.Handled || !IsActive
            || DataContext is not MainViewModel { IsViewSetting: false, IsPresetEditorOpen: false } model
            || FindFocusedEditor() is not null || IsPopupInputSource(args.Source)
            || _openLayerHelp?.IsOpen == true || ExportPresets.HasOpenMenu) return;

        if (mode is { } selected)
        {
            args.Handled = true;
            if (model.CanChangeMode)
            {
                model.SelectModeCommand.Execute(selected);
                Focus();
            }
            return;
        }

        // Preserve Enter on controls reached by keyboard navigation.
        if (args.Source is Button button && button != StartExportButton && button.Classes.Contains(":focus-visible"))
            return;
        args.Handled = true;
        if (_exportEnterHeld) return;
        _exportEnterHeld = true;
        if (model.StartExportCommand.CanExecute(null)) model.StartExportCommand.Execute(null);
    }
}
