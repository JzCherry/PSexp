using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using PsExporter.Services;
using PsExporter.ViewModels;

namespace PsExporter.Views;

public partial class MainWindow
{
    private bool _dockRetractionQueued;
    private bool _dockRetractionRequiresInactive;
    private string _dockRetractionReason = "";
    private bool _dockHadForeground;
    private bool _dockMustFinishRetraction;

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        _exportEnterHeld = false;
        _hasProbedCurrentActivation = false;
        EndInputEditing();
        if (_isDocked && _isDockRevealed)
            TryQueueDockRetraction("deactivated");
        if (!_isSnapAnimating && !_dockRetractionQueued && DataContext is MainViewModel model)
            model.FlushParameterMemory();
    }

    private void OnDockWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty && WindowState == WindowState.Minimized)
            TryQueueDockRetraction("window-state-minimized");
    }

    private bool CanRetractDock()
        => _isDocked && !_isClosing && !_isHiddenToTray && !_isRestoringPlacement
            && _placementRestorationCompleted && _dockedEdge != DockEdge.None;

    // 主窗口自己的请求入口，不安装全局键盘/鼠标钩子。
    private bool TryQueueDockRetraction(string reason)
    {
        if (!CanRetractDock()) return false;
        if (!_isDockRevealed && !_isSnapAnimating && WindowState != WindowState.Minimized) return true;
        if (_dockRetractionQueued)
        {
            if (reason != "deactivated")
            {
                _dockRetractionRequiresInactive = false;
                _dockRetractionReason = reason;
            }
            return true;
        }
        _dockRetractionQueued = true;
        _dockRetractionRequiresInactive = reason == "deactivated";
        _dockRetractionReason = reason;
        var generation = _edgeAnimationGeneration;
        Dispatcher.UIThread.Post(() =>
        {
            var inactiveOnly = _dockRetractionRequiresInactive;
            var source = _dockRetractionReason;
            _dockRetractionQueued = false;
            if (!CanRetractDock() || generation != _edgeAnimationGeneration) return;
            // 只比较主窗口自身。Avalonia 的激活状态有时会晚于系统焦点变化。
            if (inactiveOnly && IsDockWindowForeground()) return;
            if (DataContext is MainViewModel diagnostics)
                diagnostics.LogDockAnimation($"[dock] retract-request source={source} state={WindowState} active={IsActive}");
            if (WindowState == WindowState.Minimized)
            {
                var animationOpacity = Opacity;
                Opacity = 0;
                if (OperatingSystem.IsWindows() && TryGetPlatformHandle() is { } handle)
                    _ = ShowWindow(handle.Handle, 4); // SW_SHOWNOACTIVATE，避免恢复时抢走其他应用焦点。
                else
                    WindowState = WindowState.Normal;
                Position = _isSnapAnimating && !_pointerAnimationActive ? _edgeCanvasPosition : DockLivePosition;
                if (_isSnapAnimating) Opacity = animationOpacity;
            }
            if (!_isDockRevealed && !_isSnapAnimating)
            {
                Opacity = 0;
                Position = _offscreenParkPos;
                WindowTaskSwitcher.Configure(this, excluded: true);
                ShowDockIcon();
                return;
            }
            _dockMustFinishRetraction = true;
            StartEdgeAnimation(_dockHiddenPos, DockAnimationMotion.HideDurationMs);
        });
        return true;
    }

    private bool IsDockWindowForeground() => OperatingSystem.IsWindows()
        ? TryGetPlatformHandle() is { } handle && GetForegroundWindow() == handle.Handle
        : IsActive;
}
