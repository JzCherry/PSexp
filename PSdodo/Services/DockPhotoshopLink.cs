using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PsExporter.Models;

namespace PsExporter.Services;

/// <summary>
/// 贴边悬浮球与 Photoshop 前台状态的关联判定与 Windows 前台监听。
/// 仅使用 OUTOFCONTEXT WinEventHook，不轮询前台、不安装全局键鼠钩子。
/// </summary>
[Obfuscation(Exclude = true, ApplyToMembers = true)]
public static class DockPhotoshopLink
{
    public const uint EventSystemForeground = 0x0003;
    public const uint EventSystemMinimizeStart = 0x0016;
    public const uint EventSystemMinimizeEnd = 0x0017;

    private const uint GwHwndNext = 2;
    private const uint GwOwner = 4;
    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x00000008L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExLayered = 0x00080000L;
    private const int DwmwaCloaked = 14;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int MaxZOrderScan = 128;

    /// <summary>纯状态判定：是否应在屏幕边缘显示悬浮球。</summary>
    public static bool ShouldShowDockIcon(
        bool isDocked,
        bool isDockRevealed,
        bool isSnapAnimating,
        bool isHiddenToTray,
        bool isClosing,
        bool onlyWhenPhotoshopForeground,
        bool isPhotoshopForeground)
    {
        if (!isDocked || isDockRevealed || isSnapAnimating || isHiddenToTray || isClosing)
            return false;
        if (onlyWhenPhotoshopForeground && !isPhotoshopForeground)
            return false;
        return true;
    }

    public static bool IsPhotoshopProcessExecutable(string? executablePath)
        => !string.IsNullOrWhiteSpace(executablePath)
            && string.Equals(
                Path.GetFileName(executablePath),
                AppSettings.DefaultSummonHotkeyProgram,
                StringComparison.OrdinalIgnoreCase);

    public static bool IsPhotoshopWindowClass(string? className)
        => !string.IsNullOrWhiteSpace(className)
            && (className.Equals("Photoshop", StringComparison.OrdinalIgnoreCase)
                || className.Equals("OWL.DocumentWindow", StringComparison.OrdinalIgnoreCase)
                || className.Equals("PSFloatC", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 判断当前面向用户的前台应用是否为 Photoshop。
    /// 若系统前台暂时仍停留在已收纳至屏外的 PSexp 主窗口，则顺着 Z 序向下找第一个真实可见的非本进程顶层窗口。
    /// </summary>
    internal static bool IsPhotoshopActiveForDock(IntPtr minimizingWindow = default)
    {
        if (!OperatingSystem.IsWindows()) return true;

        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == minimizingWindow || IsIconic(foreground))
            return false;

        if (GetWindowThreadProcessId(foreground, out var pid) == 0 || pid == 0)
            return false;

        var selfPid = (uint)Environment.ProcessId;
        if (pid != selfPid)
            return IsPhotoshopWindow(foreground, pid);

        return IsTopVisibleOtherWindowPhotoshop(selfPid, minimizingWindow);
    }

    private static bool IsTopVisibleOtherWindowPhotoshop(uint selfPid, IntPtr minimizingWindow)
    {
        var current = GetTopWindow(IntPtr.Zero);
        for (var scanned = 0; current != IntPtr.Zero && scanned < MaxZOrderScan; scanned++, current = GetWindow(current, GwHwndNext))
        {
            if (current == minimizingWindow || !IsWindowVisible(current) || IsIconic(current))
                continue;

            if (GetWindowThreadProcessId(current, out var pid) == 0 || pid == 0 || pid == selfPid)
                continue;

            if (IsDwmCloaked(current))
                continue;

            var exStyle = GetWindowLongPtr(current, GwlExStyle).ToInt64();
            // 跳过任务栏、系统置顶浮层等 Topmost 窗口，定位普通应用层最上方的真实窗口。
            if ((exStyle & WsExTopmost) != 0)
                continue;
            if ((exStyle & WsExToolWindow) != 0 && GetWindow(current, GwOwner) == IntPtr.Zero)
                continue;
            if ((exStyle & WsExTransparent) != 0 && (exStyle & WsExLayered) != 0)
                continue;

            var className = GetWindowClassName(current);
            if (IsShellChromeClass(className))
                continue;

            if (!GetWindowRect(current, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
                continue;

            return IsPhotoshopWindow(current, pid, className);
        }

        return false;
    }

    private static bool IsShellChromeClass(string? className)
        => !string.IsNullOrWhiteSpace(className)
            && (className.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase)
                || className.Equals("Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase));

    private static bool IsPhotoshopWindow(IntPtr hwnd, uint pid, string? preloadedClassName = null)
    {
        var executable = GetProcessExecutable(pid);
        if (executable is not null)
            return IsPhotoshopProcessExecutable(executable);

        // 极端权限受限时按窗口类名兜底。
        var className = preloadedClassName ?? GetWindowClassName(hwnd);
        return IsPhotoshopWindowClass(className);
    }

    private static bool IsDwmCloaked(IntPtr hwnd)
        => DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static string? GetProcessExecutable(uint processId)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid) return null;
        var path = new StringBuilder(512);
        var length = (uint)path.Capacity;
        if (QueryFullProcessImageName(process, 0, path, ref length)) return path.ToString();
        if (Marshal.GetLastPInvokeError() != 122) return null; // ERROR_INSUFFICIENT_BUFFER
        length = 32768;
        path.EnsureCapacity((int)length);
        return QueryFullProcessImageName(process, 0, path, ref length) ? path.ToString() : null;
    }

    private static string? GetWindowClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(128);
        var length = GetClassName(hwnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString(0, length) : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "GetTopWindow")]
    private static extern IntPtr GetTopWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindow")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "IsIconic")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("kernel32.dll", EntryPoint = "OpenProcess")]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint size);
}

/// <summary>监听前台窗口变化及最小化/还原事件，供贴边悬浮球实时响应 Photoshop 前台状态。</summary>
[Obfuscation(Exclude = true, ApplyToMembers = true)]
internal sealed class DockPhotoshopForegroundMonitor : IDisposable
{
    private readonly Action<uint, IntPtr> _onEvent;
    private readonly WinEventProc _callback;
    private IntPtr _foregroundHook;
    private IntPtr _minimizeHook;
    private bool _disposed;

    internal bool IsActive => _foregroundHook != IntPtr.Zero;

    internal DockPhotoshopForegroundMonitor(Action<uint, IntPtr> onEvent)
    {
        _onEvent = onEvent;
        _callback = HandleWinEvent;
        if (!OperatingSystem.IsWindows()) return;
        _foregroundHook = SetWinEventHook(
            DockPhotoshopLink.EventSystemForeground,
            DockPhotoshopLink.EventSystemForeground,
            IntPtr.Zero, _callback, 0, 0, 0);
        _minimizeHook = SetWinEventHook(
            DockPhotoshopLink.EventSystemMinimizeStart,
            DockPhotoshopLink.EventSystemMinimizeEnd,
            IntPtr.Zero, _callback, 0, 0, 0);
    }

    private void HandleWinEvent(IntPtr hook, uint eventType, IntPtr window,
        int objectId, int childId, uint threadId, uint eventTime)
    {
        if (_disposed) return;
        if (hook == _foregroundHook || hook == _minimizeHook)
            _onEvent(eventType, window);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_foregroundHook != IntPtr.Zero)
        {
            var hook = _foregroundHook;
            _foregroundHook = IntPtr.Zero;
            UnhookWinEvent(hook);
        }
        if (_minimizeHook != IntPtr.Zero)
        {
            var hook = _minimizeHook;
            _minimizeHook = IntPtr.Zero;
            UnhookWinEvent(hook);
        }
    }

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window,
        int objectId, int childId, uint threadId, uint eventTime);

    [DllImport("user32.dll", EntryPoint = "SetWinEventHook")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
        WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll", EntryPoint = "UnhookWinEvent")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);
}
