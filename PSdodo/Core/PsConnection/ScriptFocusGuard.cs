using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using PsExporter.Core.Logging;
using PsExporter.Services;

namespace PsExporter.Core.PsConnection;

/// <summary>
/// Photoshop 执行我们派过去的脚本时会自己抢到前台，主窗随之失去激活：标题栏变灰、Enter 与
/// Shift+1~4 失效，贴边状态下还会被当成“用户点了别处”而收回——可用户并没有做任何切换动作。
/// 这里只在“用户最后一次输入仍落在 PSexp、之后没有任何新输入、抢前台的是 Photoshop 本体而不是
/// 它弹出的从属窗口”时把前台拿回来；用户主动切走、Photoshop 弹框、前台属于别的程序，一律不抢。
/// </summary>
public static class ScriptFocusGuard
{
    // 判决读数：谁抢了前台、有没有拿回来。现场确认稳定后把 LogReclaim 翻成 false 收掉。
    private static readonly bool LogReclaim = true;
    private const uint GwOwner = 4;

    // 一次脚本调用期间最多主动拿回这么多次；再多说明 Photoshop 在反复抢前台，
    // 继续抢会变成来回打架，所以停手并把证据留在日志里。脚本结束的收尾重试不受此限。
    private const int MaxPromptReclaims = 3;

    private static readonly object Gate = new();
    private static readonly Lazy<FileLogger> Log = new(() => new FileLogger());
    private static Lease? _active;

    public enum Decision
    {
        /// <summary>脚本开始时前台就不在 PSexp，没有可拿回的东西。</summary>
        NoSnapshot,
        /// <summary>当前没有前台窗口。</summary>
        NoForeground,
        /// <summary>前台还是自己，不需要动作。</summary>
        StillOurs,
        /// <summary>期间出现过新的键鼠输入＝用户自己切走的。</summary>
        UserInput,
        /// <summary>前台是 Photoshop 弹出的从属窗口（对话框等），前台留给它。</summary>
        OwnedDialog,
        /// <summary>前台是别的程序，不是 Photoshop 抢的。</summary>
        NotPhotoshop,
        /// <summary>自己的窗口此刻是工具窗口，不适合抢前台。</summary>
        ToolWindow,
        Reclaim
    }

    /// <summary>纯判定：只吃已经查好的事实，便于自测覆盖每一条分支。</summary>
    public static Decision Decide(IntPtr ourWindow, IntPtr foreground, uint capturedTick, uint currentTick,
        bool foregroundHasOwner, bool foregroundIsPhotoshop, bool ourWindowIsToolWindow)
    {
        if (ourWindow == IntPtr.Zero) return Decision.NoSnapshot;
        if (foreground == IntPtr.Zero) return Decision.NoForeground;
        if (foreground == ourWindow) return Decision.StillOurs;
        if (currentTick != capturedTick) return Decision.UserInput;
        if (foregroundHasOwner) return Decision.OwnedDialog;
        if (!foregroundIsPhotoshop) return Decision.NotPhotoshop;
        if (ourWindowIsToolWindow) return Decision.ToolWindow;
        return Decision.Reclaim;
    }

    /// <summary>脚本派发前调用：只有前台确实是本进程窗口时才建立租约，否则返回 null（全程不干预）。</summary>
    internal static Lease? Begin(string stage)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return null;
        _ = GetWindowThreadProcessId(window, out var processId);
        if (processId != (uint)Environment.ProcessId) return null;
        // 前台是我们自己的从属窗口（托盘菜单、弹层、对话框）时不建租约：它们转头就消失，拿回它没有意义。
        if (GetWindow(window, GwOwner) != IntPtr.Zero) return null;
        if (ReadLastInputTick() is not { } tick) return null;
        var lease = new Lease(stage, window, tick);
        // 嵌套调用（导出前的文档识别 + 导出本身）共用最外层租约，收尾也只由它负责。
        lock (Gate) _active ??= lease;
        return lease;
    }

    /// <summary>主窗失去激活时调用：把当前脚本调用期间的非用户失焦立刻纠正回来。</summary>
    internal static bool ReclaimActive(string reason)
    {
        Lease? lease;
        lock (Gate) lease = _active;
        return lease is not null && lease.Reclaim(reason);
    }

    internal sealed class Lease : IDisposable
    {
        private readonly string _stage;
        private readonly IntPtr _window;
        private readonly uint _inputTick;
        private int _promptReclaims;
        private bool _disposed;

        internal Lease(string stage, IntPtr window, uint inputTick)
        {
            _stage = stage;
            _window = window;
            _inputTick = inputTick;
        }

        internal bool Reclaim(string reason) => TryReclaim(reason, final: false);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            bool owner;
            lock (Gate)
            {
                owner = ReferenceEquals(_active, this);
                if (owner) _active = null;
            }
            // 脚本已经结束，不存在和 Photoshop 来回抢前台的问题，所以收尾由最外层租约再确认一次。
            if (owner) TryReclaim("script-end", final: true);
        }

        private bool TryReclaim(string reason, bool final)
        {
            var foreground = GetForegroundWindow();
            var (foregroundName, foregroundProcessId) = DescribeWindow(foreground);
            var tick = ReadLastInputTick();
            // 读不到输入时钟就当作用户动过手：宁可不抢，也不抢错。
            var decision = tick is null
                ? Decision.UserInput
                : Decide(_window, foreground, _inputTick, tick.Value,
                    foreground != IntPtr.Zero && GetWindow(foreground, GwOwner) != IntPtr.Zero,
                    foregroundName.Equals("Photoshop", StringComparison.OrdinalIgnoreCase),
                    WindowTaskSwitcher.IsToolWindow(_window));
            // 前台还在自己手里＝什么都没发生，不写盘。
            if (decision is Decision.NoForeground or Decision.StillOurs) return false;
            if (decision != Decision.Reclaim)
            {
                Write(decision, reason, foregroundName, foregroundProcessId, tick, final, skipped: "no");
                return false;
            }
            if (!final && _promptReclaims >= MaxPromptReclaims)
            {
                Write(decision, reason, foregroundName, foregroundProcessId, tick, final, skipped: "cap");
                return false;
            }
            if (!final) _promptReclaims++;
            // 查进程期间前台可能又变了；只在确认还是那个窗口时才动手。
            var set = GetForegroundWindow() == foreground && SetForegroundWindow(_window);
            Write(decision, reason, foregroundName, foregroundProcessId, tick, final, set: set);
            return set;
        }

        private void Write(Decision decision, string reason, string foregroundName, uint foregroundProcessId,
            uint? tick, bool final, string? skipped = null, bool? set = null)
        {
            if (!LogReclaim) return;
            var message = $"[focus] stage={_stage} reason={reason}{(final ? " final" : "")} decision={decision}"
                + (skipped is null ? "" : $" skipped={skipped}")
                + $" fg={foregroundName}:{foregroundProcessId}"
                + $" tick={_inputTick}/{(tick is { } value ? value.ToString() : "n/a")}"
                + (set is null ? "" : $" set={set}");
            var logger = Log.Value;
            _ = Task.Run(() => logger.Debug(message));
        }
    }

    private static (string Name, uint ProcessId) DescribeWindow(IntPtr window)
    {
        if (window == IntPtr.Zero) return ("none", 0);
        _ = GetWindowThreadProcessId(window, out var processId);
        var name = "unknown";
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            name = process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { }
        return (name, processId);
    }

    private static uint? ReadLastInputTick()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? info.Tick : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Tick;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}
