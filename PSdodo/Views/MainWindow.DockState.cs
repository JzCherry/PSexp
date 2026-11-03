using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using PsExporter.Core.PsConnection;
using PsExporter.Services;
using PsExporter.ViewModels;

namespace PsExporter.Views;

public partial class MainWindow
{
    internal UxpScriptBridge? ScriptBridge { get; init; }

    private bool _dockRetractionQueued;
    private bool _dockRetractionRequiresInactive;
    private string _dockRetractionReason = "";

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        _exportEnterHeld = false;
        _hasProbedCurrentActivation = false;
        EndInputEditing();
        // 起步/交接时恢复任务栏样式可能短暂失活，不能把内部呈现调整当成点击外部。
        if (_isDocked && _isDockRevealed && (!_isSnapAnimating || _snapClock.IsRunning))
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
            // 文件选择框、更新确认框及本程序菜单不应使父窗口自动收纳。
            if (inactiveOnly && (IsActive || IsForegroundOwnedByThisApplication())) return;
            if (inactiveOnly && TryGetPlatformHandle() is { } window &&
                ScriptBridge?.IsPhotoshopDispatchDeactivation(window.Handle) == true)
            {
                if (DataContext is MainViewModel model)
                    model.LogDockAnimation("[dock] 保持展开：Photoshop 脚本派发引起的失焦");
                return;
            }
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
                Position = _isSnapAnimating ? _edgeCanvasPosition : _dockExpandedPos;
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
            StartEdgeAnimation(_dockHiddenPos, DockAnimationMotion.HideDurationMs);
            // 明确离开/最小化的意图不能被尚未移出原感应区的指针立即反向。
            _edgeCanReverseOnPointerReturn = false;
        });
        return true;
    }

    private bool IsForegroundOwnedByThisApplication()
    {
        if (!OperatingSystem.IsWindows()) return OwnedWindows.Count > 0;
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        _ = GetWindowThreadProcessId(foreground, out var processId);
        if (processId == (uint)Environment.ProcessId) return true;
        var owner = GetAncestor(foreground, 3); // GA_ROOTOWNER
        return owner != IntPtr.Zero && owner == TryGetPlatformHandle()?.Handle;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);
}
