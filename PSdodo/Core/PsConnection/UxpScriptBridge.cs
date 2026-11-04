using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PsExporter.Services;

namespace PsExporter.Core.PsConnection;

/// <summary>
/// UXP 脚本桥：通过 .psjs 文件关联触发 PS 的 UXP 运行时执行脚本
/// 原理：通过系统文件关联打开 .psjs 时，
/// PS 会用 UXP 运行时执行（支持 require("photoshop")、batchPlay、executeAsModal）。
/// 限制：无返回值，无法同步等待完成。调用方需通过其他方式（如文件数量变化）判断结果。
/// </summary>
public sealed class UxpScriptBridge
{
    private const int DispatchDelayMs = 500;
    private const int CleanupDelayMs = 2_000;
    private sealed record DispatchFocus(IntPtr Window, uint InputTick);

    /// <summary>
    /// 执行 UXP 脚本：写入临时 .psjs 文件（UTF8 BOM），用文件关联打开。
    /// 返回临时文件路径（调用方负责清理）。
    /// </summary>
    public async Task<UxpResult> ExecuteAsync(string scriptContent, CancellationToken ct = default)
    {
        // 写临时 .psjs 文件（UTF8 with BOM —— PS 识别 .psjs 需要 BOM）
        var tempDir = Path.GetTempPath();
        var tempFile = Path.Combine(tempDir, $"psx_{Guid.NewGuid():N}.psjs");

        // 写入 UTF8 with BOM
        var utf8WithBom = new UTF8Encoding(true);
        await File.WriteAllTextAsync(tempFile, scriptContent, utf8WithBom, ct);

        var dispatched = false;
        DispatchFocus? focus = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            focus = OperatingSystem.IsWindows() ? CaptureDispatchFocus() : null;
            if (OperatingSystem.IsWindows())
                await StaHelper.RunAsync(() => DispatchWithoutActivation(tempFile), ct);
            else
                using (System.Diagnostics.Process.Start(new ProcessStartInfo(tempFile) { UseShellExecute = true })) { }
            dispatched = true;

            // Photoshop 接收脚本的短窗口内只检查一次焦点。
            await Task.Delay(DispatchDelayMs, ct);
            if (focus is not null) RestoreDispatchFocus(focus);

            return new UxpResult
            {
                TempFilePath = tempFile,
                Success = true
            };
        }
        catch (OperationCanceledException)
        {
            // 请求已交给 Photoshop 后，取消仅停止应用等待，不能删除它可能尚未读取的脚本。
            if (!dispatched)
                try { File.Delete(tempFile); } catch { /* ignore */ }
            throw;
        }
        catch (Exception ex)
        {
            // 清理临时文件
            if (!dispatched)
                try { File.Delete(tempFile); } catch { /* ignore */ }
            return new UxpResult
            {
                TempFilePath = tempFile,
                Success = false,
                Error = ex
            };
        }
    }

    private static bool DispatchWithoutActivation(string path)
    {
        const uint noAsync = 0x00000100;
        const uint noErrorDialog = 0x00000400;
        const int showNoActivate = 4;
        var request = new ShellExecuteInfo
        {
            Size = (uint)Marshal.SizeOf<ShellExecuteInfo>(),
            Mask = noAsync | noErrorDialog,
            File = path,
            Show = showNoActivate
        };
        if (!ShellExecuteEx(ref request))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 未能通过文件关联打开 Photoshop UXP 脚本");
        return true;
    }

    private static DispatchFocus? CaptureDispatchFocus()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return default;
        _ = GetWindowThreadProcessId(window, out var processId);
        var input = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return processId == (uint)Environment.ProcessId && GetLastInputInfo(ref input)
            ? new DispatchFocus(window, input.Tick) : null;
    }

    private static void RestoreDispatchFocus(DispatchFocus before)
    {
        var foreground = FindPhotoshopFocusHandoff(before);
        const uint owner = 4; // GW_OWNER：保留 Photoshop 自己打开的提示框。
        if (foreground == IntPtr.Zero || GetWindow(foreground, owner) != IntPtr.Zero) return;

        _ = GetWindowThreadProcessId(before.Window, out var processId);
        if (processId == (uint)Environment.ProcessId && GetForegroundWindow() == foreground
            && !WindowTaskSwitcher.IsToolWindow(before.Window))
            _ = SetForegroundWindow(before.Window);
    }

    private static IntPtr FindPhotoshopFocusHandoff(DispatchFocus before)
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == before.Window) return IntPtr.Zero;
        var input = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref input) || input.Tick != before.InputTick) return IntPtr.Zero;

        _ = GetWindowThreadProcessId(foreground, out var processId);
        if (processId == 0 || processId == (uint)Environment.ProcessId) return IntPtr.Zero;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            return process.ProcessName.Equals("Photoshop", StringComparison.OrdinalIgnoreCase) &&
                GetForegroundWindow() == foreground ? foreground : IntPtr.Zero;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public uint Size;
        public uint Mask;
        public IntPtr Window;
        public string? Verb;
        public string File;
        public string? Parameters;
        public string? Directory;
        public int Show;
        public IntPtr Instance;
        public IntPtr ItemIdList;
        public string? Class;
        public IntPtr ClassKey;
        public uint HotKey;
        public IntPtr IconOrMonitor;
        public IntPtr Process;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Tick;
    }

    [DllImport("shell32.dll", EntryPoint = "ShellExecuteExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo request);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo input);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    /// <summary>清理临时 .psjs 文件</summary>
    public async Task CleanupTempFileAsync(string tempFilePath)
    {
        if (string.IsNullOrEmpty(tempFilePath)) return;
        try
        {
            await Task.Delay(CleanupDelayMs, CancellationToken.None);
            try { File.Delete(tempFilePath); } catch { /* ignore */ }
        }
        catch { /* ignore */ }
    }
}

/// <summary>UXP 脚本执行结果</summary>
public sealed class UxpResult
{
    /// <summary>临时 .psjs 文件路径（调用方需清理）</summary>
    public string TempFilePath { get; set; } = string.Empty;

    /// <summary>是否成功触发（不代表脚本执行成功，只代表文件关联触发成功）</summary>
    public bool Success { get; set; }

    /// <summary>触发时的异常（如果有）</summary>
    public Exception? Error { get; set; }
}
