using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PsExporter.ViewModels;

namespace PsExporter.Views;

public partial class MainWindow
{
    private readonly DispatcherTimer _settingsPageFadeTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _settingsPageFadeClock = new();
    private double _settingsPageFadeStart;
    private ExportCommandWindow? _exportCommandWindow;

    private void OnHighlightPaletteKeyDown(object? sender, KeyEventArgs args)
    {
        var last = HighlightPalette.ItemCount - 1;
        if (last < 0 || args.Key is not (Key.Left or Key.Right or Key.Home or Key.End)) return;
        var index = args.Key switch
        {
            Key.Home => 0,
            Key.End => last,
            Key.Left => Math.Max(0, HighlightPalette.SelectedIndex - 1),
            _ => Math.Min(last, HighlightPalette.SelectedIndex + 1)
        };
        HighlightPalette.SelectedIndex = index;
        HighlightPalette.ContainerFromIndex(index)?.Focus(NavigationMethod.Directional);
        args.Handled = true;
    }

    private void OnSettingsNavigationRequested(bool updates)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_isClosing || DataContext is not MainViewModel { IsViewSetting: true }) return;
            SettingsScrollViewer.Offset = default;
            if (updates) { SettingsPageHost.UpdateLayout(); SettingsVersionCard.BringIntoView(); }
        }, DispatcherPriority.Loaded);
    }

    private void OnClearSettingsSearchClick(object? sender, RoutedEventArgs args)
    {
        // Let the command clear the query first, then return typing focus to the visible search box.
        Dispatcher.UIThread.Post(() =>
        {
            if (IsActive && SettingsSearchInput.IsEffectivelyVisible) SettingsSearchInput.Focus();
        }, DispatcherPriority.Loaded);
    }

    private void OnOpenExportCommandClick(object? sender, RoutedEventArgs args)
    {
        if (_exportCommandWindow is { } openWindow)
        {
            if (openWindow.WindowState == WindowState.Minimized)
                openWindow.WindowState = WindowState.Normal;
            openWindow.SyncFromMainInterface();
            openWindow.Activate();
            return;
        }

        if (DataContext is not MainViewModel model) return;
        var window = new ExportCommandWindow { DataContext = model };
        PlaceExportCommandWindow(window);
        window.Closed += (_, _) => _exportCommandWindow = null;
        _exportCommandWindow = window;
        window.Show(this);
    }

    private void PlaceExportCommandWindow(ExportCommandWindow window)
    {
        if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is not { } screen) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var width = (int)Math.Ceiling(window.Width * scale);
        var height = (int)Math.Ceiling(window.Height * scale);
        var ownerWidth = (int)Math.Ceiling(Bounds.Width * scale);
        var right = Position.X + ownerWidth;
        var left = Position.X - width;
        var offset = (int)Math.Round(24 * scale);
        var x = right + width <= area.Right ? right
            : left >= area.X ? left
            : Position.X + offset;
        var y = Position.Y + offset;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = new PixelPoint(
            Math.Clamp(x, area.X, Math.Max(area.X, area.Right - width)),
            Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - height)));
    }

    private void BeginSettingsPageFade()
    {
        if (_isClosing) return;
        // One restrained fade when opening settings; search typing never animates the whole list.
        _settingsPageFadeStart = Math.Min(SettingsPageHost.Opacity, 0.72);
        SettingsPageHost.Opacity = _settingsPageFadeStart;
        _settingsPageFadeClock.Restart();
        _settingsPageFadeTimer.Start();
    }

    private void OnSettingsPageFadeTick(object? sender, EventArgs e)
    {
        var progress = Math.Clamp(_settingsPageFadeClock.Elapsed.TotalMilliseconds / 170, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        SettingsPageHost.Opacity = _settingsPageFadeStart + (1 - _settingsPageFadeStart) * eased;
        if (progress >= 1) StopSettingsPageFade();
    }

    private void StopSettingsPageFade()
    {
        _settingsPageFadeTimer.Stop();
        _settingsPageFadeClock.Reset();
        SettingsPageHost.Opacity = 1;
    }
}
