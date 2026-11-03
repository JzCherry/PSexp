using System.Diagnostics;
using System.Text.Json;
using PsExporter.Core.Logging;

namespace PsExporter.Core.PsConnection;

/// <summary>等待脚本明确回执；原生画板后台命令还须核对独立临时目录的文件数量与写入完成状态。</summary>
public static class UxpTaskWatcher
{
    public const int DefaultTimeoutMs = 30 * 60 * 1000;
    public const int FirstReceiptTimeoutMs = 30_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static async Task<PsBridgeResult> WaitForCompletionAsync(
        string resultPath, string exportPath, Action<string, int>? onProgress = null,
        CancellationToken ct = default, int timeoutMs = DefaultTimeoutMs,
        ExportDirectorySnapshot? backgroundSnapshot = null, int? expectedFileCount = null,
        Action<string>? onBackgroundSubmitted = null)
    {
        var timer = Stopwatch.StartNew();
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> files = [];
        var receivedReceipt = false;
        while (timer.ElapsedMilliseconds < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            UxpTaskStatus? status = null;
            string json = "";
            try
            {
                json = await File.ReadAllTextAsync(resultPath, ct);
                status = JsonSerializer.Deserialize<UxpTaskStatus>(json, JsonOptions);
            }
            catch (IOException) { /* 文件尚未创建，或脚本正在写入。 */ }
            catch (JsonException) { /* 下一轮读取完整 JSON。 */ }
            if (status is not null)
            {
                receivedReceipt = true;
                var names = status.Files ?? [];
                var validNames = names.Where(name => !string.IsNullOrWhiteSpace(name)
                    && Path.GetFileName(name) == name && !Path.IsPathRooted(name)
                    && (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                files = validNames.Select(name => Path.Combine(exportPath, name)).Where(File.Exists).ToArray();
                foreach (var file in files)
                    if (reported.Add(file)) onProgress?.Invoke(Path.GetFileName(file), reported.Count);
                if (status.Done)
                {
                    if (status.Success && status.BackgroundExport)
                    {
                        onBackgroundSubmitted?.Invoke(json);
                        return await WaitForBackgroundFilesAsync(status, json, exportPath, backgroundSnapshot,
                            expectedFileCount, onProgress, ct, Math.Max(1, timeoutMs - (int)timer.ElapsedMilliseconds));
                    }
                    var success = status.Success && files.Count > 0 && files.Count == names.Count;
                    return new PsBridgeResult
                    {
                        Success = success, ReturnValue = json, OutputFiles = files, ExportedCount = files.Count,
                        Error = success ? null : new IOException(string.IsNullOrWhiteSpace(status.Error)
                            ? "UXP 导出未完成或输出文件缺失" : status.Error)
                    };
                }
            }
            if (!receivedReceipt && timer.ElapsedMilliseconds >= FirstReceiptTimeoutMs)
                return PsBridgeResult.Failed(new TimeoutException("Photoshop 未返回任务回执，请检查脚本启动和目录权限：" + resultPath));
            await Task.Delay(250, ct);
        }
        return new PsBridgeResult
        {
            Success = false, OutputFiles = files, ExportedCount = files.Count,
            Error = new TimeoutException("等待 UXP 导出回执超时；Photoshop 可能仍在执行，已保留输出和任务记录：" + resultPath)
        };
    }

    private static async Task<PsBridgeResult> WaitForBackgroundFilesAsync(
        UxpTaskStatus status, string receipt, string exportPath, ExportDirectorySnapshot? before,
        int? expectedFileCount, Action<string, int>? onProgress, CancellationToken ct, int timeoutMs)
    {
        var format = status.Format?.ToLowerInvariant();
        if (before is null || format is not ("jpg" or "png"))
            return PsBridgeResult.Failed(new InvalidDataException("后台导出缺少有效的目录快照或图片格式"));
        var expected = status.ExpectedFileCount is > 0 ? status.ExpectedFileCount
            : expectedFileCount is > 0 ? expectedFileCount : null;
        var timer = Stopwatch.StartNew();
        var completed = await ExportFileWatcher.WaitForChangesAsync(exportPath, format, before,
            expectedFileCount: expected, timeoutMs: timeoutMs, onProgress: onProgress, ct: ct,
            requireCompletedImages: true, firstChangeTimeoutMs: timeoutMs);
        var success = !completed.TimedOut && completed.Count > 0;
        var countDescription = expected is > 0 ? expected.Value.ToString() : "未知（按文件写入稳定判断）";
        return new PsBridgeResult
        {
            Success = success,
            ReturnValue = receipt,
            OutputFiles = completed.Files,
            ExportedCount = completed.Count,
            Error = success ? null : new TimeoutException(
                $"等待 Photoshop 后台图片写入超时：预期 {countDescription} 张，已确认 {completed.Count} 张；Photoshop 可能仍在导出，已保留任务记录和图片：{exportPath}"),
            Logs = [new PsLogEntry
            {
                Level = success ? PsLogLevel.Info : PsLogLevel.Warn,
                Message = $"后台写入确认：预期={countDescription}；已确认={completed.Count}；等待={timer.ElapsedMilliseconds}ms；文件须非空、未被写入占用、图片尾标记完整且目录连续稳定 {ExportFileWatcher.StablePeriodMs}ms。"
            }]
        };
    }

    private sealed class UxpTaskStatus
    {
        public bool Done { get; set; }
        public bool Success { get; set; }
        public bool BackgroundExport { get; set; }
        public int? ExpectedFileCount { get; set; }
        public string? Format { get; set; }
        public List<string>? Files { get; set; }
        public string? Error { get; set; }
    }
}
