using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PsExporter.Core.PsConnection;

/// <summary>
/// UXP 脚本桥：通过 .psjs 文件关联触发 PS 的 UXP 运行时执行脚本
/// 原理：通过系统文件关联打开 .psjs 时，
/// PS 会用 UXP 运行时执行（支持 require("photoshop")、batchPlay、executeAsModal）。
/// 限制：无返回值，无法同步等待完成。调用方需通过其他方式（如文件数量变化）判断结果。
/// </summary>
public sealed class UxpScriptBridge
{
    private const int CleanupDelayMs = 2_000;

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
        // Photoshop 读取 .psjs 时会自己抢到前台；租约记下派发前的前台与输入时钟，之后按需拿回来。
        using var focus = ScriptFocusGuard.Begin("uxp-dispatch");
        try
        {
            ct.ThrowIfCancellationRequested();
            if (OperatingSystem.IsWindows())
                await StaHelper.RunAsync(() => DispatchWithoutActivation(tempFile), ct);
            else
                using (System.Diagnostics.Process.Start(new ProcessStartInfo(tempFile) { UseShellExecute = true })) { }
            dispatched = true;

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

    [DllImport("shell32.dll", EntryPoint = "ShellExecuteExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo request);

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
