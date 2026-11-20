using System;
using Avalonia.Threading;
using PsExporter.Services;
using PsExporter.ViewModels;

namespace PsExporter.Views;

public partial class MainWindow
{
    private DockPhotoshopForegroundMonitor? _dockPhotoshopMonitor;

    private bool IsDockIconOnlyWhenPhotoshopForeground
        => DataContext is MainViewModel { DockIconOnlyWhenPhotoshopForeground: true };

    private bool IsDockIconAllowedByForeground(IntPtr minimizingWindow = default)
        => !IsDockIconOnlyWhenPhotoshopForeground
            || !OperatingSystem.IsWindows()
            || DockPhotoshopLink.IsPhotoshopActiveForDock(minimizingWindow);

    private void SyncDockPhotoshopLink(IntPtr minimizingWindow = default)
    {
        var shouldWatch = OperatingSystem.IsWindows()
            && !_isClosing
            && !_isHiddenToTray
            && _isDocked
            && IsDockIconOnlyWhenPhotoshopForeground;

        if (shouldWatch)
        {
            if (_dockPhotoshopMonitor is null)
            {
                _dockPhotoshopMonitor = new DockPhotoshopForegroundMonitor(OnDockPhotoshopForegroundEvent);
                if (!_dockPhotoshopMonitor.IsActive && DataContext is MainViewModel model)
                    model.AppendLog("Error", "无法监测前台程序，悬浮球关联 Photoshop 未生效");
            }
        }
        else
        {
            StopDockPhotoshopLink();
        }

        if (_isClosing || _isHiddenToTray) return;

        // 正在收回动画期间切到非 Photoshop 时，提前收起悬浮球避免渐显闪现。
        if (_isDocked && _isSnapAnimating && _edgeMotion.IsHiding
            && !IsDockIconAllowedByForeground(minimizingWindow))
        {
            if (_dockIconWindow is { Opacity: > 0 })
                CloseDockIcon();
            return;
        }

        if (!_isDocked || _isDockRevealed || _isSnapAnimating) return;

        var monitorFailed = shouldWatch && _dockPhotoshopMonitor is { IsActive: false };
        var shouldShow = monitorFailed || DockPhotoshopLink.ShouldShowDockIcon(
            _isDocked,
            _isDockRevealed,
            _isSnapAnimating,
            _isHiddenToTray,
            _isClosing,
            IsDockIconOnlyWhenPhotoshopForeground,
            IsDockIconAllowedByForeground(minimizingWindow));

        if (shouldShow)
        {
            if (_dockIconWindow is not { Opacity: > 0 })
                ShowDockIcon();
        }
        else if (_dockIconWindow is { Opacity: > 0 })
        {
            CloseDockIcon();
        }
    }

    private void StopDockPhotoshopLink()
    {
        var monitor = _dockPhotoshopMonitor;
        _dockPhotoshopMonitor = null;
        monitor?.Dispose();
    }

    private void OnDockPhotoshopForegroundEvent(uint eventType, IntPtr window)
    {
        if (_isClosing) return;
        if (eventType == DockPhotoshopLink.EventSystemMinimizeStart)
        {
            SyncDockPhotoshopLink(minimizingWindow: window);
            Dispatcher.UIThread.Post(() => SyncDockPhotoshopLink(), DispatcherPriority.Background);
            return;
        }
        SyncDockPhotoshopLink();
    }
}
