using System;
using System.Diagnostics;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using PsExporter.Core.Automation;
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

    private async void OnCopyAutomationCommandClick(object? sender, RoutedEventArgs args)
    {
        if (sender is not Button { Content: "复制指令" } button || DataContext is not MainViewModel model
            || Environment.ProcessPath is not { } executable || Clipboard is not { } clipboard) return;

        var isPreset = button.CommandParameter is "preset";
        var presetId = isPreset ? model.CurrentPresetCommandId : null;
        if (isPreset && presetId is null) return;

        button.Content = "复制中…";
        try
        {
            await clipboard.SetTextAsync(ExportInvocation.BuildPowerShellCommand(executable, presetId));
            button.Content = "已复制";
        }
        catch (Exception)
        {
            button.Content = "复制失败";
        }

        await Task.Delay(1400);
        button.Content = "复制指令";
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
