using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using PsExporter.Services;
using PsExporter.ViewModels;

namespace PsExporter.Views;

public partial class MainWindow
{
    private const uint SummonHotkeyMessage = 0x0312; // WM_HOTKEY
    private const int SummonHotkeyId = 0x51E7;
    private bool _summonHotkeyAttached;
    private bool _dockRevealedByHotkey;
    private PixelPoint? _pointerDockPosition;
    private bool _resummonPointerAfterMotion;
    private PixelPoint DockLivePosition => _pointerDockPosition ?? _dockExpandedPos;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>抓取状态只存在设置项视图模型里，窗口读它，不另存一份。</summary>
    private bool IsCapturingSummonHotkey => DataContext is MainViewModel { IsSummonHotkeyCapturing: true };

    /// <summary>窗口已有原生句柄后调用；热键消息仍由既有的 WndProc 子类分发，不额外起线程。</summary>
    internal void InitializeSummonHotkey()
    {
        if (_summonHotkeyAttached || !OperatingSystem.IsWindows()
            || DataContext is not MainViewModel model) return;
        InstallWndProcSubclass();
        if (!_isSubclassed) return;
        _summonHotkeyAttached = true;
        model.SummonHotkeyRegistrationRequested += OnSummonHotkeyRegistrationRequested;
        Deactivated += OnSummonHotkeyWindowDeactivated;
        ApplySummonHotkeyRegistration();
    }

    private void DetachSummonHotkey()
    {
        if (!_summonHotkeyAttached) return;
        _summonHotkeyAttached = false;
        if (DataContext is MainViewModel model)
            model.SummonHotkeyRegistrationRequested -= OnSummonHotkeyRegistrationRequested;
        Deactivated -= OnSummonHotkeyWindowDeactivated;
        UnregisterSummonHotkey();
    }

    private void OnSummonHotkeyRegistrationRequested() => ApplySummonHotkeyRegistration();

    private void OnSummonHotkeyWindowDeactivated(object? sender, EventArgs e) => StopSummonHotkeyCapture();

    private void UnregisterSummonHotkey()
    {
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero) UnregisterHotKey(hwnd, SummonHotkeyId);
    }

    private void ApplySummonHotkeyRegistration()
    {
        UnregisterSummonHotkey();
        if (IsCapturingSummonHotkey) return;
        if (DataContext is not MainViewModel model) return;
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        // 空组合键 = 关闭：TryParse 失败就走上面的注销，不再注册。
        if (!SummonHotkey.TryParse(model.SummonHotkey, out var key, out var modifiers)
            || !SummonHotkey.TryGetVirtualKey(key, out var virtualKey))
            return;
        if (RegisterHotKey(hwnd, SummonHotkeyId, SummonHotkey.NativeModifiers(modifiers), virtualKey))
        {
            model.LogDockAnimation($"[hotkey] registered key={model.SummonHotkey}");
            return;
        }

        var error = Marshal.GetLastPInvokeError();
        model.AppendLog("Error", $"唤起热键 {model.SummonHotkey} 注册失败（Win32 {error}）");
        // 设置项写入还在栈上，提示挪到下一帧再落，避免边写边读同一状态。
        ReportSummonHotkeyStatus(model, error == 1409
            ? $"唤起热键 {model.SummonHotkey} 已被其他程序占用"
            : $"唤起热键 {model.SummonHotkey} 注册失败（Win32 {error}）");
    }

    private void OnSummonHotkeyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;
        if (IsCapturingSummonHotkey)
        {
            StopSummonHotkeyCapture();
            return;
        }
        UnregisterSummonHotkey();
        model.IsSummonHotkeyCapturing = true;
        if (sender is Button button) button.Focus();
    }

    private void OnSummonHotkeyLostFocus(object? sender, RoutedEventArgs e) => StopSummonHotkeyCapture();

    private void StopSummonHotkeyCapture()
    {
        if (DataContext is not MainViewModel model || !IsCapturingSummonHotkey) return;
        model.IsSummonHotkeyCapturing = false;
        ApplySummonHotkeyRegistration();
    }

    private void OnSummonHotkeyCaptureKeyDown(object? sender, KeyEventArgs args)
    {
        if (!IsCapturingSummonHotkey || DataContext is not MainViewModel model) return;
        args.Handled = true;
        if (args.Key == Key.Escape)
        {
            StopSummonHotkeyCapture();
            return;
        }
        // 退格 = 清空 = 关闭热键；Esc 才是"取消，保留原键"。
        if (args.Key == Key.Back)
        {
            if (model.SummonHotkey.Length > 0) model.SummonHotkey = "";
            ReportSummonHotkeyStatus(model, "唤起热键已清空：不再占用任何系统按键");
            StopSummonHotkeyCapture();
            return;
        }
        if (SummonHotkey.TryFormat(args.Key, args.KeyModifiers, out var text))
        {
            model.SummonHotkey = text;
            // 单用功能键会把这个键从其他程序手里拿走，选它的当场说一次。
            if (args.KeyModifiers == KeyModifiers.None)
                ReportSummonHotkeyStatus(model, $"{text} 已单独作为唤起键，其他程序里的 {text} 交给 PSexp");
            StopSummonHotkeyCapture();
            return;
        }
        ReportSummonHotkeyStatus(model, SummonHotkey.TryGetVirtualKey(args.Key, out _)
            ? "字母、数字和小键盘数字要搭配 Ctrl / Alt / Shift / Win，只有 F1–F12 能单用"
            : "唤起热键只支持字母、数字、小键盘数字和 F1–F12");
    }

    private void OnSummonHotkeyMessage() => Dispatcher.UIThread.Post(() =>
    {
        _animationViewModel?.LogDockAnimation(
            $"[hotkey] received key={_animationViewModel.SummonHotkey} docked={_isDocked} animating={_isSnapAnimating}");
        SummonFromHotkey();
    });

    private void SummonFromHotkey()
    {
        if (_isClosing) return;
        if (_isRestoringPlacement)
        {
            ActivateAfterPlacementRestore(SummonFromHotkey);
            return;
        }
        if (DataContext is MainViewModel { FollowsPointerOnSummon: true } model)
        {
            if (_isDocked && model.EnableEdgeSnap && model.SummonReturnAnimation
                && TryGetPointerSummonPlacement(out var position, out var screen)
                && screen.Bounds == Screens.ScreenFromPoint(_dockExpandedPos)?.Bounds)
            {
                SummonDockAtPointer(position);
                return;
            }
            ActivateFromExternalRequest();
            return;
        }
        if (_isDocked)
        {
            if (_isDockRevealed) return;
            _dockRevealedByHotkey = true;
            StartEdgeAnimation(_dockExpandedPos, DockAnimationMotion.RevealDurationMs);
            BringToForeground();
            return;
        }
        if (!IsVisible || WindowState == WindowState.Minimized)
            ActivateFromExternalRequest(keepWindowPosition: true);
    }

    private void SummonDockAtPointer(PixelPoint position)
    {
        if (_isSnapAnimating)
        {
            _resummonPointerAfterMotion = true;
            return;
        }
        if (_pointerDockPosition is not null && _isDockRevealed && !IsDockWindowForeground())
        {
            // 热键可能先于失焦后的收纳请求到达，先收回，再从原入口展开。
            _resummonPointerAfterMotion = true;
            StartEdgeAnimation(_dockHiddenPos, DockAnimationMotion.HideDurationMs);
            return;
        }
        _pointerDockPosition = position;
        _dockRevealedByHotkey = true;
        _edgeHoverTimer.Stop();
        if (_isDockRevealed) Position = position;
        else StartEdgeAnimation(_dockExpandedPos, DockAnimationMotion.RevealDurationMs);
        BringToForeground();
        if (!_isSnapAnimating) _edgeHoverTimer.Start();
    }

    private static void ReportSummonHotkeyStatus(MainViewModel model, string text)
        => Dispatcher.UIThread.Post(() => model.SettingsStatusText = text, DispatcherPriority.Loaded);
}
