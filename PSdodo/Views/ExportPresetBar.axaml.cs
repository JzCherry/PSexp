using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PsExporter.ViewModels;

namespace PsExporter.Views;

public partial class ExportPresetBar : UserControl
{
    private MainViewModel? _model;
    public ExportPresetBar()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_model is not null) _model.PropertyChanged -= OnModelChanged;
            _model = DataContext as MainViewModel;
            if (_model is not null) _model.PropertyChanged += OnModelChanged;
        };
    }
    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(MainViewModel.IsPresetEditorOpen) or nameof(MainViewModel.IsPresetNameEditor)) || _model?.IsPresetEditorOpen != true) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_model?.IsPresetEditorOpen == true && _model.IsPresetNameEditor && IsEffectivelyVisible
                && TopLevel.GetTopLevel(this) is Window { IsActive: true })
            { PresetNameInput.Focus(); PresetNameInput.SelectAll(); }
        }, DispatcherPriority.Loaded);
    }
    private void OnPresetChoiceClick(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: ExportPresetListItem choice } && _model is { } model)
            model.ApplyPresetCommand.Execute(choice);
        PresetPicker.Flyout?.Hide();
    }
    private void OnMenuActionClick(object? sender, RoutedEventArgs args)
        => Dispatcher.UIThread.Post(() => PresetMore.Flyout?.Hide()); // Let the button command finish before detaching popup content.
    // Called before the window's blank-area drag handler. A dismissal click must
    // neither start a native move loop nor activate a control underneath a menu.
    public bool DismissOnOutsidePointer(PointerPressedEventArgs args, bool allowEditorClickThrough)
    {
        if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return false;
        var source = args.Source as Visual;
        for (var visual = source; visual is not null; visual = visual.GetVisualParent())
            if (visual is FlyoutPresenter or MenuFlyoutPresenter or Avalonia.Controls.ContextMenu) return false; // Keep popup content interactive.

        if (PresetPicker.Flyout?.IsOpen == true || PresetMore.Flyout?.IsOpen == true)
        {
            HideMenus();
            args.Handled = true;
            return true;
        }
        if (_model?.IsPresetEditorOpen != true || IsWithin(source, PresetEditor)) return false;
        // Header buttons keep their normal action (save, picker, more).
        for (var visual = source; visual is not null && IsWithin(visual, this); visual = visual.GetVisualParent())
            if (visual is Button) return false;

        CancelEditor();
        if (allowEditorClickThrough) return false;
        args.Handled = true;
        return true;
    }

    public bool DismissOnEscape()
    {
        if (PresetPicker.Flyout?.IsOpen == true || PresetMore.Flyout?.IsOpen == true)
        { HideMenus(); return true; }
        if (_model?.IsPresetEditorOpen != true) return false;
        CancelEditor();
        return true;
    }

    private void HideMenus()
    {
        PresetPicker.Flyout?.Hide();
        PresetMore.Flyout?.Hide();
    }

    private void CancelEditor()
    {
        _model?.CancelPresetEditorCommand.Execute(null);
        // Do not leave keyboard focus on the now-hidden name input.
        if (TopLevel.GetTopLevel(this) is Window { IsActive: true } window)
            window.Focus();
    }

    private static bool IsWithin(Visual? visual, Visual ancestor)
    {
        for (; visual is not null; visual = visual.GetVisualParent())
            if (ReferenceEquals(visual, ancestor)) return true;
        return false;
    }
}
