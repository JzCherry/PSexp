using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PsExporter.Core.Logging;
using PsExporter.Core.Scripting;

namespace PsExporter.Core.PsConnection;

/// <summary>
/// Windows 平台通过 Photoshop.Application COM 对象执行 ExtendScript
/// </summary>
public sealed class WindowsComBridge : IPsBridge
{

    public bool IsAvailable => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public PsBridgeResult Execute(string script, string? logFilePath = null)
    {
        if (!IsAvailable)
            throw new PlatformNotSupportedException("WindowsComBridge 仅支持 Windows");
        return StaHelper.Run(() => ExecuteCore(script, logFilePath));
    }

    public Task<PsBridgeResult> ExecuteAsync(string script, string? logFilePath = null, CancellationToken ct = default)
        => StaHelper.RunAsync(() => ExecuteCore(script, logFilePath), ct);

    private static PsBridgeResult ExecuteCore(string script, string? logFilePath)
    {
        var wrapped = WrapScript(script, logFilePath);
        dynamic? ps = null;
        var connectionLogs = new List<PsLogEntry>();
        // DoJavaScript 期间 Photoshop 可能自己抢到前台，让主窗白丢激活；用户没操作就拿回来。
        using var focus = ScriptFocusGuard.Begin("com-script");
        try
        {
            ps = RunningPhotoshopConnection.Attach(out var connection);
            connectionLogs.Add(new PsLogEntry { Level = PsLogLevel.Debug, Message = connection });

            object? returnValue;
            try
            {
                // DoJavaScript(JavaScript, Args, JsEngine) —— Engine: 1 = ExtendScript
                returnValue = ps.DoJavaScript(wrapped, new object[] { }, 1);
            }
            catch (Exception ex) when (ex is not PsNotConnectedException)
            {
                throw new PsNotConnectedException("Photoshop 未响应或被中断: " + ex.Message, ex);
            }

            var retStr = returnValue?.ToString() ?? string.Empty;
            var logs = connectionLogs.Concat(ReadLogs(logFilePath)).ToArray();
            var hasScriptResult = TryReadScriptResult(
                retStr,
                out var scriptSucceeded,
                out var exportedCount,
                out var scriptError);
            if (!hasScriptResult)
            {
                scriptSucceeded =
                    !string.IsNullOrWhiteSpace(retStr) &&
                    logs.All(log => log.Level != PsLogLevel.Error);
            }
            return new PsBridgeResult
            {
                Success = scriptSucceeded,
                Error = scriptSucceeded ? null : new InvalidOperationException(scriptError ?? "Photoshop 脚本执行失败"),
                ReturnValue = retStr,
                Logs = logs,
                ExportedCount = exportedCount
            };
        }
        catch (Exception ex)
        {
            return new PsBridgeResult
            {
                Success = false,
                Error = ex,
                ReturnValue = string.Empty,
                Logs = connectionLogs.Concat(ReadLogsSafe(logFilePath)).ToArray()
            };
        }
        finally
        {
            if (ps is not null)
            {
                try { Marshal.ReleaseComObject(ps); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>包装用户脚本：去掉 #target 指令、捕获 $.writeln 到日志文件、统一返回值</summary>
    /// <remarks>
    /// 约定：脚本末尾应为 <c>__psx_result = &lt;value&gt;;</c> 形式（探针/导出脚本都已遵循）。
    /// 包装器只负责定义 __psx_result、劫持 $.writeln、try/catch 兜底。
    /// </remarks>
    private static string WrapScript(string userScript, string? logFilePath)
    {
        // 去掉 #target xxx / $$#target xxx 这类预处理指令（DoJavaScript 已在 PS 上下文，不需要）
        var lines = userScript.Replace("\r\n", "\n").Split('\n');
        var filtered = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("#target", StringComparison.OrdinalIgnoreCase)) continue;
            if (trimmed.StartsWith("$$#target", StringComparison.OrdinalIgnoreCase)) continue;
            if (trimmed.StartsWith("$$") && trimmed.Contains("#target")) continue;
            filtered.Add(line);
        }
        var cleaned = string.Join("\n", filtered);

        if (string.IsNullOrEmpty(logFilePath))
        {
            return
                "var __psx_result = '';\n" +
                "try {\n" + cleaned + "\n}\n" +
                "catch(__e) { __psx_result = ''; }\n" +
                "__psx_result;";
        }

        var logEscaped = ScriptParameterInjector.EscapeJsString(logFilePath);
        return
            "var __psx_result = '';\n" +
            "var __psx_logFile = new File(\"" + logEscaped + "\");\n" +
            "__psx_logFile.encoding = \"UTF-8\";\n" +
            "__psx_logFile.open(\"w\");\n" +
            "$.writeln = function(s) { try { __psx_logFile.writeln(String(s)); } catch(__e) {} };\n" +
            "try {\n" + cleaned + "\n}\n" +
            "catch(__e) {\n" +
            "    try { __psx_logFile.writeln(\"PSX_LOG|error|wrapper: \" + __e); } catch(__e2) {}\n" +
            "    __psx_result = \"\";\n" +
            "}\n" +
            "finally { try { __psx_logFile.close(); } catch(__e) {} }\n" +
            "__psx_result;";
    }

    private static IReadOnlyList<PsLogEntry> ReadLogs(string? logFilePath)
    {
        var list = new List<PsLogEntry>();
        if (string.IsNullOrEmpty(logFilePath) || !File.Exists(logFilePath))
            return list;
        try
        {
            foreach (var line in File.ReadAllLines(logFilePath))
                list.Add(PsLogEntry.Parse(line));
        }
        catch
        {
            // 读取失败不影响主流程
        }
        finally
        {
            try { if (File.Exists(logFilePath)) File.Delete(logFilePath!); } catch { /* ignore */ }
        }
        return list;
    }

    private static IReadOnlyList<PsLogEntry> ReadLogsSafe(string? logFilePath)
    {
        try { return ReadLogs(logFilePath); }
        catch { return []; }
    }

    private static bool TryReadScriptResult(
        string returnValue,
        out bool succeeded,
        out int exportedCount,
        out string? error)
    {
        succeeded = true;
        exportedCount = 0;
        error = null;
        try
        {
            using var document = JsonDocument.Parse(returnValue);
            if (!document.RootElement.TryGetProperty("success", out var success))
                return false;

            if (success.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return false;

            succeeded = success.GetBoolean();
            if (document.RootElement.TryGetProperty("count", out var count) &&
                count.TryGetInt32(out var parsedCount))
            {
                exportedCount = Math.Max(0, parsedCount);
            }
            if (!succeeded)
            {
                if (document.RootElement.TryGetProperty("error", out var errorValue))
                    error = errorValue.GetString();
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
