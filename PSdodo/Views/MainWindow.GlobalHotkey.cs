using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Win32.SafeHandles;
using PsExporter.Services;
using PsExporter.ViewModels;

namespace PsExporter.Views;

public partial class MainWindow
{
    private const uint SummonHotkeyMessage = 0x0312; // WM_HOTKEY
    private const int SummonHotkeyId = 0x51E7;
    private const uint ForegroundWindowEvent = 0x0003; // EVENT_SYSTEM_FOREGROUND
    private bool _summonHotkeyAttached;
    private IntPtr _summonHotkeyWindow;
    private bool _summonHotkeyRegistered;
    private IntPtr _summonForegroundHook;
    private ForegroundWinEventProc? _summonForegroundCallback;
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

    private delegate void ForegroundWinEventProc(IntPtr hook, uint eventType, IntPtr window,
        int objectId, int childId, uint threadId, uint eventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
        ForegroundWinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint size);

    /// <summary>抓取状态只存在设置项视图模型里，窗口读它，不另存一份。</summary>
    private bool IsCapturingSummonHotkey => DataContext is MainViewModel { IsSummonHotkeyCapturing: true };

    /// <summary>窗口已有原生句柄后调用；热键消息仍由既有的 WndProc 子类分发，不额外起线程。</summary>
    internal void InitializeSummonHotkey()
    {
        if (_summonHotkeyAttached || !OperatingSystem.IsWindows()
            || DataContext is not MainViewModel model) return;
        InstallWndProcSubclass();
        if (!_isSubclassed) return;
        _summonHotkeyWindow = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        _summonHotkeyAttached = true;
        model.SummonHotkeyRegistrationRequested += OnSummonHotkeyRegistrationRequested;
        Deactivated += OnSummonHotkeyWindowDeactivated;
        ApplySummonHotkeyRegistration();
    }

    private void DetachSummonHotkey()
    {
        FinishSummonProgramPick(restoreWindow: false);
        if (!_summonHotkeyAttached) return;
        _summonHotkeyAttached = false;
        if (DataContext is MainViewModel model)
            model.SummonHotkeyRegistrationRequested -= OnSummonHotkeyRegistrationRequested;
        Deactivated -= OnSummonHotkeyWindowDeactivated;
        StopWatchingSummonForeground();
        UnregisterSummonHotkey();
        _summonHotkeyWindow = IntPtr.Zero;
    }

    private void OnSummonHotkeyRegistrationRequested() => ApplySummonHotkeyRegistration();

    private void OnSummonHotkeyWindowDeactivated(object? sender, EventArgs e) => StopSummonHotkeyCapture();

    private void UnregisterSummonHotkey()
    {
        if (!_summonHotkeyRegistered) return;
        _summonHotkeyRegistered = false;
        UnregisterHotKey(_summonHotkeyWindow, SummonHotkeyId);
    }

    private void ApplySummonHotkeyRegistration()
    {
        UnregisterSummonHotkey();
        RefreshSummonHotkeyRegistration();
    }

    private void RefreshSummonHotkeyRegistration()
    {
        if (!_summonHotkeyAttached || _summonHotkeyWindow == IntPtr.Zero || IsCapturingSummonHotkey || _isPickingSummonProgram
            || DataContext is not MainViewModel model
            || !SummonHotkey.TryParse(model.SummonHotkey, out var key, out var modifiers)
            || !SummonHotkey.TryGetVirtualKey(key, out var virtualKey))
        {
            StopWatchingSummonForeground();
            UnregisterSummonHotkey();
            return;
        }

        if (model.SummonHotkeyProgram.Length == 0)
            StopWatchingSummonForeground();
        else if (!WatchSummonForeground(model) || !IsSummonProgramForeground(model))
        {
            UnregisterSummonHotkey();
            return;
        }
        if (_summonHotkeyRegistered) return;
        if (RegisterHotKey(_summonHotkeyWindow, SummonHotkeyId, SummonHotkey.NativeModifiers(modifiers), virtualKey))
        {
            _summonHotkeyRegistered = true;
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

    private bool WatchSummonForeground(MainViewModel model)
    {
        if (_summonForegroundHook != IntPtr.Zero) return true;
        // OUTOFCONTEXT：在现有 UI 消息循环接收前台变化，不拦截键盘或鼠标。
        _summonForegroundCallback ??= OnSummonForegroundChanged;
        _summonForegroundHook = SetWinEventHook(ForegroundWindowEvent, ForegroundWindowEvent,
            IntPtr.Zero, _summonForegroundCallback, 0, 0, 0);
        if (_summonForegroundHook != IntPtr.Zero) return true;
        model.AppendLog("Error", "无法监测前台程序，绑定的唤起热键未注册");
        ReportSummonHotkeyStatus(model, "无法监测前台程序，绑定的唤起热键未启用");
        return false;
    }

    private void OnSummonForegroundChanged(IntPtr hook, uint eventType, IntPtr window,
        int objectId, int childId, uint threadId, uint eventTime)
    {
        if (hook == _summonForegroundHook) RefreshSummonHotkeyRegistration();
    }

    private void StopWatchingSummonForeground()
    {
        if (_summonForegroundHook == IntPtr.Zero) return;
        var hook = _summonForegroundHook;
        _summonForegroundHook = IntPtr.Zero;
        UnhookWinEvent(hook);
    }

    private static bool IsSummonProgramForeground(MainViewModel model)
        => model.SummonHotkeyProgram.Length == 0
            || SummonHotkey.MatchesProgram(model.SummonHotkeyProgram, GetWindowExecutable(GetForegroundWindow()));

    private static string? GetWindowExecutable(IntPtr window, bool excludeThisProcess = false)
    {
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var processId) == 0) return null;
        if (excludeThisProcess && processId == (uint)Environment.ProcessId) return null;
        const uint queryLimitedInformation = 0x1000;
        using var process = OpenProcess(queryLimitedInformation, false, processId);
        if (process.IsInvalid) return null;
        var path = new StringBuilder(512);
        uint length = (uint)path.Capacity;
        if (QueryFullProcessImageName(process, 0, path, ref length)) return path.ToString();
        if (Marshal.GetLastPInvokeError() != 122) return null; // ERROR_INSUFFICIENT_BUFFER
        length = 32768;
        path.EnsureCapacity((int)length);
        return QueryFullProcessImageName(process, 0, path, ref length) ? path.ToString() : null;
    }

    private void OnSummonHotkeyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;
        if (IsCapturingSummonHotkey)
        {
            StopSummonHotkeyCapture();
            return;
        }
        model.IsSummonHotkeyCapturing = true;
        ApplySummonHotkeyRegistration();
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
            if (args.KeyModifiers == KeyModifiers.None)
                ReportSummonHotkeyStatus(model, model.SummonHotkeyProgram.Length == 0
                    ? $"{text} 已设为全局唤起键，会占用其他程序里的 {text}"
                    : $"{text} 仅在 {model.SummonHotkeyProgramDisplay} 位于前台时用作唤起键");
            StopSummonHotkeyCapture();
            return;
        }
        ReportSummonHotkeyStatus(model, SummonHotkey.TryGetVirtualKey(args.Key, out _)
            ? "字母、数字和小键盘数字要搭配 Ctrl / Alt / Shift / Win，只有 F1–F12 能单用"
            : "唤起热键只支持字母、数字、小键盘数字和 F1–F12");
    }

    private void OnSummonHotkeyMessage() => Dispatcher.UIThread.Post(() =>
    {
        if (!_summonHotkeyRegistered || IsCapturingSummonHotkey || DataContext is not MainViewModel model
            || !IsSummonProgramForeground(model)) return;
        model.LogDockAnimation(
            $"[hotkey] received key={model.SummonHotkey} docked={_isDocked} animating={_isSnapAnimating}");
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
