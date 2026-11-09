using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PsExporter.Core.Logging;
using PsExporter.Core.PsConnection;
using PsExporter.Core.Scripting;
using PsExporter.Models;

namespace PsExporter.Services;

public sealed class ExportService : IExportService
{
    private readonly UxpScriptBridge _uxpBridge;
    private readonly FileLogger _logger;
    private readonly IPsBridge? _comBridge;

    public ExportService(UxpScriptBridge uxpBridge, FileLogger? logger = null, IPsBridge? comBridge = null)
    {
        _uxpBridge = uxpBridge;
        _logger = logger ?? new FileLogger();
        _comBridge = comBridge;
    }

    // 三个快速导出走 UXP（.psjs 文件关联触发，支持 batchPlay + executeAsModal）
    public async Task<PsBridgeResult> QuickExportAsync(ExportSettings s, ExportProgressHandler? onProgress = null, CancellationToken ct = default)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var finalDirectory = s.ExportPath;
        var tempDirectory = Path.Combine(Path.GetTempPath(), "PSexp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var delivered = false;
        try
        {
            var temporarySettings = CloneWithExportPath(s, tempDirectory);
            var result = await ExecuteUxpScriptAsync(
                ScriptNames.ExportQuick,
                BuildCommonArgs(temporarySettings),
                temporarySettings,
                onProgress: null,
                ct);
            var scriptAndReceiptMs = timer.ElapsedMilliseconds;
            var merged = MergeQuickExportOutputs(result, finalDirectory, s.ImageName, s.AutoRename, onProgress);
            _logger.Info($"[quick-export] 分段耗时 scriptAndReceiptMs={scriptAndReceiptMs} mergeMs={timer.ElapsedMilliseconds - scriptAndReceiptMs} totalMs={timer.ElapsedMilliseconds} success={merged.Success}");
            delivered = merged.Success;
            return merged;
        }
        finally
        {
            try
            {
                if (delivered && Directory.Exists(tempDirectory))
                    Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // 临时目录清理失败不覆盖导出结果。
            }
        }
    }

    public async Task<PsBridgeResult> ExportAllArtboardsAsync(ExportSettings s, ExportProgressHandler? onProgress = null, CancellationToken ct = default)
        => await ExportArtboardsAsync(ScriptNames.ExportAllArtboards, s, onProgress, ct);

    public async Task<PsBridgeResult> ExportSelectedArtboardsAsync(ExportSettings s, ExportProgressHandler? onProgress = null, CancellationToken ct = default)
        => await ExportArtboardsAsync(ScriptNames.ExportSelectedArtboards, s, onProgress, ct);

    private async Task<PsBridgeResult> ExportArtboardsAsync(
        string scriptName,
        ExportSettings settings,
        ExportProgressHandler? onProgress,
        CancellationToken ct)
    {
        var finalDirectory = ResolveArtboardExportPath(settings);
        var tempDirectory = Path.Combine(Path.GetTempPath(), "PSexp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var delivered = false;
        try
        {
            var temporarySettings = CloneWithExportPath(settings, tempDirectory);
            var result = await ExecuteUxpScriptAsync(
                scriptName,
                BuildCommonArgs(temporarySettings),
                temporarySettings,
                onProgress,
                ct);
            var merged = MergeArtboardOutputs(result, finalDirectory, settings.AutoRename, settings.FilePrefix);
            delivered = merged.Success;
            return merged;
        }
        finally
        {
            try
            {
                if (delivered && Directory.Exists(tempDirectory))
                    Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // 临时目录清理失败不覆盖导出结果。
            }
        }
    }

    private static ExportSettings CloneWithExportPath(ExportSettings source, string exportPath) => new()
    {
        ExportPath = exportPath,
        DocumentName = source.DocumentName,
        Format = source.Format,
        Quality = source.Quality,
        AutoRename = source.AutoRename,
        FileName = source.FileName,
        FilePrefix = source.FilePrefix,
        SliceFolderName = source.SliceFolderName,
        ImageName = source.ImageName,
        LayerExportWithDocName = source.LayerExportWithDocName,
        LayerExportRecursive = source.LayerExportRecursive,
        LayerExportSelectedOnly = source.LayerExportSelectedOnly,
        LayerTopLevelSelectedOnly = source.LayerTopLevelSelectedOnly,
        LayerExportShowHint = source.LayerExportShowHint,
        ExportPathOption = source.ExportPathOption,
        ExpectedFileCount = source.ExpectedFileCount,
        SelectedArtboardIds = source.SelectedArtboardIds
    };

    public static PsBridgeResult MergeArtboardOutputs(
        PsBridgeResult result,
        string finalDirectory,
        bool autoRename,
        string filePrefix = "")
        => MergeNamedOutputs(result, finalDirectory, autoRename, filePrefix, "画板");

    private static PsBridgeResult MergeNamedOutputs(
        PsBridgeResult result, string finalDirectory, bool autoRename, string filePrefix, string outputKind)
    {
        if (result.OutputFiles.Count == 0) return result;

        var movedFiles = new List<string>(result.OutputFiles.Count);
        var currentSource = string.Empty;
        var currentTarget = finalDirectory;
        try
        {
            Directory.CreateDirectory(finalDirectory);
            var prefix = ExportFilePrefix.Normalize(filePrefix);
            foreach (var sourcePath in result.OutputFiles)
            {
                var originalName = Path.GetFileName(sourcePath);
                var desiredPath = Path.Combine(finalDirectory, prefix.Length == 0 ? originalName : prefix + "_" + originalName);
                var targetPath = autoRename ? GetAvailableFilePath(desiredPath) : desiredPath;
                currentSource = sourcePath;
                currentTarget = targetPath;
                File.Move(sourcePath, targetPath, overwrite: !autoRename);
                movedFiles.Add(targetPath);
            }

            return new PsBridgeResult
            {
                Success = result.Success,
                ReturnValue = result.ReturnValue,
                ExportedCount = movedFiles.Count,
                OutputFiles = movedFiles,
                Error = result.Error,
                Logs = result.Logs
            };
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or IOException
                                   or NotSupportedException
                                   or UnauthorizedAccessException)
        {
            return new PsBridgeResult
            {
                Success = false,
                ReturnValue = result.ReturnValue,
                ExportedCount = movedFiles.Count,
                OutputFiles = movedFiles,
                Error = new IOException($"{outputKind}归并失败：源={currentSource}；目标={currentTarget}；覆盖={!autoRename}；HResult=0x{ex.HResult:X8}；{ex.Message}", ex),
                Logs = result.Logs
            };
        }
    }

    public Task<PsBridgeResult> ExportLayersAsync(ExportSettings s, ExportProgressHandler? onProgress = null, CancellationToken ct = default)
        => ExportLayerBatchAsync(s, sliceMode: false, onProgress, ct);

    public Task<PsBridgeResult> ExportSlicesAsync(ExportSettings s, ExportProgressHandler? onProgress = null, CancellationToken ct = default)
        => ExportLayerBatchAsync(CloneWithExportPath(s, ResolveSliceExportPath(s)), sliceMode: true, onProgress, ct);

    public async Task<PsBridgeResult> SaveAndCloseDocumentAsync(int documentId, string documentPath)
    {
        if (_comBridge is { IsAvailable: true })
        {
            var escapedPath = ScriptParameterInjector.EscapeJsString(documentPath);
            var idStr = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var comScript =
                "__psx_result = (function() {\n" +
                "    var target = null;\n" +
                "    for (var i = 0; i < app.documents.length; i++) {\n" +
                "        if (app.documents[i].id === " + idStr + ") { target = app.documents[i]; break; }\n" +
                "    }\n" +
                "    if (!target) return '{\"success\":false,\"error\":\"原导出文档已不在 Photoshop 中\"}';\n" +
                "    var curPath = '';\n" +
                "    try { curPath = target.fullName ? target.fullName.fsName : ''; } catch (e) {}\n" +
                "    var expectedPath = new File(\"" + escapedPath + "\").fsName;\n" +
                "    if (curPath.toLowerCase() !== expectedPath.toLowerCase() && curPath.toLowerCase() !== \"" + escapedPath + "\".toLowerCase())\n" +
                "        return '{\"success\":false,\"error\":\"原导出文档的保存路径已变化\"}';\n" +
                "    target.close(SaveOptions.SAVECHANGES);\n" +
                "    for (var j = 0; j < app.documents.length; j++) {\n" +
                "        if (app.documents[j].id === " + idStr + ") return '{\"success\":false,\"error\":\"Photoshop 未确认关闭文档\"}';\n" +
                "    }\n" +
                "    return '{\"success\":true,\"count\":1}';\n" +
                "})();";
            var comResult = await _comBridge.ExecuteAsync(comScript, null, CancellationToken.None);
            if (comResult.Success || comResult.Error?.Message is "原导出文档已不在 Photoshop 中" or "原导出文档的保存路径已变化")
                return comResult;
            _logger.Warn("COM 保存并关闭原文档未完成，回退 UXP：" + comResult.Error?.Message);
        }

        var directory = Path.Combine(Path.GetTempPath(), "PSexp", "tasks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = ScriptParameterInjector.Inject(ScriptLibrary.GetScript("export/save_and_close"), new Dictionary<string, string>
        {
            ["RESULT_FOLDER_URL"] = ScriptParameterInjector.EscapeJsString(ToUxpFolderUrl(directory)),
            ["DOCUMENT_ID"] = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["DOCUMENT_PATH"] = ScriptParameterInjector.EscapeJsString(documentPath)
        });
        var dispatched = await _uxpBridge.ExecuteAsync(script, CancellationToken.None);
        if (!dispatched.Success)
            return PsBridgeResult.Failed(dispatched.Error ?? new IOException("保存并关闭脚本未启动"));
        var result = await UxpTaskWatcher.WaitForCompletionAsync(Path.Combine(directory, "result.json"), exportPath: null);
        if (result.Success)
        {
            // A successful terminal receipt already confirms completion. Keep the deletion
            // grace period in the background instead of delaying the next user operation.
            _ = _uxpBridge.CleanupTempFileAsync(dispatched.TempFilePath);
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }

    public static string ResolveSliceExportPath(ExportSettings settings)
    {
        var name = (settings.SliceFolderName ?? string.Empty).Trim();
        if (!ExportSettingsValidator.TryValidateSliceFolder(name, out var error)) throw new ArgumentException(error);
        return string.IsNullOrEmpty(name) ? settings.ExportPath : Path.Combine(settings.ExportPath, name);
    }

    private async Task<PsBridgeResult> ExportLayerBatchAsync(
        ExportSettings s, bool sliceMode, ExportProgressHandler? onProgress, CancellationToken ct)
    {
        var args = BuildCommonArgs(s);
        args["QUALITY"] = s.Quality.ToString();
        args["FILE_NAME"] = ScriptParameterInjector.EscapeJsString(s.FileName);
        args["SLICE_MODE"] = sliceMode ? "true" : "false";
        args["RECURSIVE"] = s.LayerExportRecursive ? "true" : "false";
        args["SELECTED_ONLY"] = (s.LayerExportRecursive ? s.LayerExportSelectedOnly : s.LayerTopLevelSelectedOnly) ? "true" : "false";
        return await ExecuteReceiptTaskAsync(ScriptLibrary.GetScript(sliceMode ? ScriptNames.ExportSlices : ScriptNames.ExportLayerBatch), args, s, onProgress, ct);
    }

    private async Task<PsBridgeResult> ExecuteReceiptTaskAsync(
        string template, Dictionary<string, string> args, ExportSettings s,
        ExportProgressHandler? onProgress, CancellationToken ct)
    {
        var taskId = Guid.NewGuid().ToString("N");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        _logger.Info($"[task={taskId}] 开始 UXP 导出；exe={Environment.ProcessPath}；temp={Path.GetTempPath()}；log={_logger.LogPath}");
        _logger.Info($"[task={taskId}] 参数={System.Text.Json.JsonSerializer.Serialize(s)}；sliceMode={args.GetValueOrDefault("SLICE_MODE", "false")}");
        try
        {
            var result = await ExecuteReceiptTaskCoreAsync(template, args, s, (name, count) =>
            {
                _logger.Debug($"[task={taskId}] 输出进度 count={count} file={System.Text.Json.JsonSerializer.Serialize(name)}");
                onProgress?.Invoke(name, count);
            }, ct, taskId);
            _logger.Info($"[task={taskId}] 结束 success={result.Success} count={result.ExportedCount} elapsedMs={timer.ElapsedMilliseconds}");
            if (result.Error is not null) _logger.Error($"[task={taskId}] 完整异常：{result.Error}");
            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.Warn($"[task={taskId}] 已取消等待；elapsedMs={timer.ElapsedMilliseconds}；Photoshop 可能继续执行，保留任务文件");
            throw;
        }
        catch (Exception error)
        {
            _logger.Error($"[task={taskId}] 调用链异常，elapsedMs={timer.ElapsedMilliseconds}：{error}");
            throw;
        }
    }

    private async Task<PsBridgeResult> ExecuteReceiptTaskCoreAsync(
        string template, Dictionary<string, string> args, ExportSettings s,
        ExportProgressHandler? onProgress, CancellationToken ct, string taskId)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(s.ExportPath);
        if (args.ContainsKey("SLICE_MODE"))
        {
            // UXP Folder.getEntries can fail even when getEntryWithUrl resolved the destination.
            // Only names that can collide with this format are needed for automatic suffixes.
            var extension = "." + s.Format.ToExtension();
            args["EXISTING_EXPORT_NAMES"] = s.AutoRename
                ? System.Text.Json.JsonSerializer.Serialize(Directory.EnumerateFiles(s.ExportPath, "*" + extension)
                    .Where(path => string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
                    .Select(Path.GetFileName))
                : "[]";
        }
        var taskDirectory = Path.Combine(Path.GetTempPath(), "PSexp", "tasks", taskId);
        Directory.CreateDirectory(taskDirectory);
        args["EXPORT_FOLDER_URL"] = ScriptParameterInjector.EscapeJsString(ToUxpFolderUrl(s.ExportPath));
        args["RESULT_FOLDER_URL"] = ScriptParameterInjector.EscapeJsString(ToUxpFolderUrl(taskDirectory));
        var script = ScriptParameterInjector.Inject(template, args);
        _logger.Info($"[task={taskId}] 输出目录={s.ExportPath} exists={Directory.Exists(s.ExportPath)}；回执目录={taskDirectory}");
        _logger.Debug($"[task={taskId}] 注入参数={System.Text.Json.JsonSerializer.Serialize(args.Where(pair => pair.Key is not ("SCRIPT_BODY" or "EXISTING_EXPORT_NAMES")).ToDictionary(pair => pair.Key, pair => pair.Value))}；脚本SHA256={Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(script)))}");
        var backgroundSnapshot = args.GetValueOrDefault("WAIT_FOR_BACKGROUND_EXPORT") == "true"
            ? ExportFileWatcher.CaptureSnapshot(s.ExportPath, s.Format.ToExtension()) : null;
        var phaseTimer = System.Diagnostics.Stopwatch.StartNew();
        var dispatched = await _uxpBridge.ExecuteAsync(script, ct);
        var dispatchMs = phaseTimer.ElapsedMilliseconds;
        _logger.Info($"[task={taskId}] 脚本启动 success={dispatched.Success} path={dispatched.TempFilePath}");
        if (dispatched.Error is not null) _logger.Error($"[task={taskId}] 脚本启动异常：{dispatched.Error}");
        if (!dispatched.Success) return PsBridgeResult.Failed(dispatched.Error ?? new IOException("UXP 脚本启动失败"));
        var result = await UxpTaskWatcher.WaitForCompletionAsync(
            Path.Combine(taskDirectory, "result.json"), s.ExportPath,
            (name, count) => onProgress?.Invoke(name, count), ct,
            backgroundSnapshot: backgroundSnapshot, expectedFileCount: s.ExpectedFileCount,
            onBackgroundSubmitted: receipt => _logger.Info($"[task={taskId}] 已提交后台导出，等待图片写入；回执={receipt}"));
        _logger.Info($"[task={taskId}] 分段耗时 dispatchMs={dispatchMs} receiptAndWriteMs={phaseTimer.ElapsedMilliseconds - dispatchMs}");
        _logger.Info($"[task={taskId}] 回执={result.ReturnValue}");
        if (backgroundSnapshot is not null)
            _logger.Info($"[task={taskId}] 后台图片写入确认 success={result.Success} count={result.ExportedCount} files={System.Text.Json.JsonSerializer.Serialize(result.OutputFiles)}");
        if (result.Success && !string.IsNullOrEmpty(result.ReturnValue))
        {
            // Retain the delayed cleanup for Photoshop, but release the confirmed export
            // immediately so quick-output merge and the completion notice do not wait 2s.
            _ = _uxpBridge.CleanupTempFileAsync(dispatched.TempFilePath);
            try { Directory.Delete(taskDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (!result.Success)
        {
            return new PsBridgeResult
            {
                Success = false, ReturnValue = result.ReturnValue,
                ExportedCount = result.ExportedCount, OutputFiles = result.OutputFiles,
                Error = result.Error,
                Logs = [.. result.Logs, new PsLogEntry
                {
                    Level = PsLogLevel.Warn,
                    Message = $"导出诊断记录已保留：{taskDirectory}；脚本：{dispatched.TempFilePath}"
                }]
            };
        }
        return result;
    }

    public static string ToUxpFolderUrl(string path)
        => "file:/" + Path.GetFullPath(path).Replace('\\', '/');

    public static PsBridgeResult MergeQuickExportOutputs(
        PsBridgeResult result,
        string finalDirectory,
        string imageName,
        bool autoRename,
        ExportProgressHandler? onProgress = null)
    {
        if (!result.Success || result.OutputFiles.Count == 0)
            return result;

        if (result.OutputFiles.Count != 1 && !string.IsNullOrWhiteSpace(imageName))
        {
            result = new PsBridgeResult
            {
                Success = result.Success,
                ReturnValue = result.ReturnValue,
                ExportedCount = result.ExportedCount,
                OutputFiles = result.OutputFiles,
                Error = result.Error,
                Logs = [.. result.Logs, new PsLogEntry
                {
                    Level = PsLogLevel.Warn,
                    Message = $"快速导出检测到 {result.OutputFiles.Count} 个文件，已保留 Photoshop 原始名称"
                }]
            };
        }

        try
        {
            Directory.CreateDirectory(finalDirectory);
            var movedFiles = new List<string>(result.OutputFiles.Count);
            foreach (var sourcePath in result.OutputFiles)
            {
                var stem = result.OutputFiles.Count == 1 && !string.IsNullOrWhiteSpace(imageName)
                    ? SanitizeFileStem(imageName)
                    : Path.GetFileNameWithoutExtension(sourcePath);
                if (string.IsNullOrWhiteSpace(stem))
                    throw new IOException("图片名称不包含有效字符");

                var desiredPath = Path.Combine(finalDirectory, stem + Path.GetExtension(sourcePath));
                var targetPath = autoRename ? GetAvailableFilePath(desiredPath) : desiredPath;
                File.Move(sourcePath, targetPath, overwrite: !autoRename);
                movedFiles.Add(targetPath);
                onProgress?.Invoke(Path.GetFileName(targetPath), movedFiles.Count);
            }

            return new PsBridgeResult
            {
                Success = true,
                ReturnValue = result.ReturnValue,
                ExportedCount = movedFiles.Count,
                OutputFiles = movedFiles,
                Logs = result.Logs
            };
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or IOException
                                   or NotSupportedException
                                   or UnauthorizedAccessException)
        {
            return new PsBridgeResult
            {
                Success = false,
                ReturnValue = result.ReturnValue,
                ExportedCount = 0,
                OutputFiles = [],
                Error = new IOException("图片已生成，但归并到导出目录失败：" + ex.Message, ex),
                Logs = result.Logs
            };
        }
    }

    public static PsBridgeResult ApplyQuickExportName(
        PsBridgeResult result,
        ExportSettings settings,
        ExportProgressHandler? onProgress = null)
    {
        if (!result.Success || result.OutputFiles.Count == 0)
            return result;

        if (string.IsNullOrWhiteSpace(settings.ImageName))
        {
            ReportOutputFiles(result.OutputFiles, onProgress);
            return result;
        }

        if (result.OutputFiles.Count != 1)
        {
            ReportOutputFiles(result.OutputFiles, onProgress);
            return new PsBridgeResult
            {
                Success = result.Success,
                ReturnValue = result.ReturnValue,
                ExportedCount = result.ExportedCount,
                OutputFiles = result.OutputFiles,
                Error = result.Error,
                Logs = [.. result.Logs, new PsLogEntry
                {
                    Level = PsLogLevel.Warn,
                    Message = $"快速导出检测到 {result.OutputFiles.Count} 个文件，已保留 Photoshop 原始名称"
                }]
            };
        }

        var sourcePath = result.OutputFiles[0];
        try
        {
            var directory = Path.GetDirectoryName(sourcePath)
                ?? throw new IOException("无法确定导出文件目录");
            var extension = Path.GetExtension(sourcePath);
            var stem = SanitizeFileStem(settings.ImageName);
            if (string.IsNullOrWhiteSpace(stem))
                throw new IOException("图片名称不包含有效字符");

            var targetPath = Path.Combine(directory, stem + extension);
            if (!Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
            {
                targetPath = settings.AutoRename
                    ? GetAvailableFilePath(targetPath)
                    : targetPath;
                File.Move(sourcePath, targetPath, overwrite: !settings.AutoRename);
            }

            onProgress?.Invoke(Path.GetFileName(targetPath), 1);
            return new PsBridgeResult
            {
                Success = true,
                ReturnValue = result.ReturnValue,
                ExportedCount = result.ExportedCount,
                OutputFiles = [targetPath],
                Logs = [.. result.Logs, new PsLogEntry
                {
                    Level = PsLogLevel.Info,
                    Message = $"快速导出文件已命名为 {Path.GetFileName(targetPath)}"
                }]
            };
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or IOException
                                   or NotSupportedException
                                   or UnauthorizedAccessException)
        {
            ReportOutputFiles(result.OutputFiles, onProgress);
            return new PsBridgeResult
            {
                Success = false,
                ReturnValue = result.ReturnValue,
                ExportedCount = result.ExportedCount,
                OutputFiles = result.OutputFiles,
                Error = new IOException("图片已导出，但自定义命名失败：" + ex.Message, ex),
                Logs = result.Logs
            };
        }
    }

    private static void ReportOutputFiles(IReadOnlyList<string> files, ExportProgressHandler? onProgress)
    {
        if (onProgress is null) return;
        for (var index = 0; index < files.Count; index++)
            onProgress(Path.GetFileName(files[index]), index + 1);
    }

    private static string SanitizeFileStem(string value)
    {
        return ExportName.Normalize(value);
    }

    private static string GetAvailableFilePath(string desiredPath)
    {
        if (!File.Exists(desiredPath)) return desiredPath;

        var directory = Path.GetDirectoryName(desiredPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(desiredPath);
        var extension = Path.GetExtension(desiredPath);
        for (var index = 1; ; index++)
        {
            var candidate = Path.Combine(directory, $"{stem}_{index}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>
    /// 执行 UXP 脚本：注入参数 -> 写临时 .psjs -> 触发文件关联 -> 等待脚本明确完成回执（带实时进度回调）
    /// </summary>
    private Task<PsBridgeResult> ExecuteUxpScriptAsync(
        string scriptName, IReadOnlyDictionary<string, string> args, ExportSettings s,
        ExportProgressHandler? onProgress, CancellationToken ct)
    {
        var body = ScriptParameterInjector.Inject(ScriptLibrary.GetScript(scriptName), args);
        return ExecuteReceiptTaskAsync(ScriptLibrary.GetScript("export/receipt_task"),
            new Dictionary<string, string>
            {
                ["SCRIPT_BODY"] = body,
                ["WAIT_FOR_BACKGROUND_EXPORT"] = scriptName is ScriptNames.ExportAllArtboards or ScriptNames.ExportSelectedArtboards ? "true" : "false",
                ["EXPORT_FORMAT"] = s.Format.ToScriptString(),
                ["EXPECTED_FILE_COUNT"] = Math.Max(0, s.ExpectedFileCount ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)
            }, s, onProgress, ct);
    }

    public static int ResolveScriptQuality(ExportSettings settings)
        => settings.Format == ExportFormat.Png ? 32 : settings.Quality;

    private static Dictionary<string, string> BuildCommonArgs(ExportSettings s) => new()
    {
        // 所有导出脚本里路径都用在单/双引号字符串内，反斜杠必须转义
        ["EXPORT_PATH"] = ScriptParameterInjector.EscapeJsString(EnsureTrailingSep(s.ExportPath)),
        ["FORMAT"] = s.Format.ToScriptString(),
        ["QUALITY"] = ResolveScriptQuality(s).ToString(),
        ["RENAME"] = s.AutoRename ? "true" : "false",
        ["IMAGE_NAME"] = ScriptParameterInjector.EscapeJsString(s.ImageName),
        ["WITH_DOC_NAME"] = s.LayerExportWithDocName ? "true" : "false"
    };

    /// <summary>根据画板保存选项解析实际导出目录。</summary>
    public static string ResolveArtboardExportPath(ExportSettings settings)
    {
        if (settings.ExportPathOption == ExportPathOption.ImageOnly)
            return settings.ExportPath;

        var baseDirectory = settings.ExportPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var rawDocumentName = Path.GetFileNameWithoutExtension(settings.DocumentName);
        var folderName = SanitizeFileStem(rawDocumentName);
        if (string.IsNullOrWhiteSpace(folderName))
            folderName = "未命名文档";

        var desiredPath = Path.Combine(baseDirectory, folderName);
        var targetPath = settings.ExportPathOption == ExportPathOption.NewFolder
            ? GetAvailableDirectoryPath(desiredPath)
            : desiredPath;

        Directory.CreateDirectory(targetPath);
        return targetPath;
    }

    private static string GetAvailableDirectoryPath(string desiredPath)
    {
        if (!Directory.Exists(desiredPath)) return desiredPath;

        for (var index = 1; ; index++)
        {
            var candidate = $"{desiredPath}_{index}";
            if (!Directory.Exists(candidate)) return candidate;
        }
    }

    private static string EnsureTrailingSep(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }
}
