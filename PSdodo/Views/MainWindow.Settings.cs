using System;
using System.Diagnostics;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PsExporter.ViewModels;

namespace PsExporter.Views;

public partial class MainWindow
{
    private readonly DispatcherTimer _settingsPageFadeTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _settingsPageFadeClock = new();
    private double _settingsPageFadeStart;

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
