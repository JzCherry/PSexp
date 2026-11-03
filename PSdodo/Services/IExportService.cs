using System;
using System.Threading;
using System.Threading.Tasks;
using PsExporter.Core.PsConnection;
using PsExporter.Models;

namespace PsExporter.Services;

/// <summary>
/// 导出进度回调
/// </summary>
/// <param name="newFileName">新生成的文件名</param>
/// <param name="exportedCount">已导出文件数</param>
public delegate void ExportProgressHandler(string newFileName, int exportedCount);

public interface IExportService
{
    Task<PsBridgeResult> QuickExportAsync(ExportSettings settings, ExportProgressHandler? onProgress = null, CancellationToken ct = default);
    Task<PsBridgeResult> ExportAllArtboardsAsync(ExportSettings settings, ExportProgressHandler? onProgress = null, CancellationToken ct = default);
    Task<PsBridgeResult> ExportSelectedArtboardsAsync(ExportSettings settings, ExportProgressHandler? onProgress = null, CancellationToken ct = default);
    Task<PsBridgeResult> ExportLayersAsync(ExportSettings settings, ExportProgressHandler? onProgress = null, CancellationToken ct = default);
    Task<PsBridgeResult> ExportSlicesAsync(ExportSettings settings, ExportProgressHandler? onProgress = null, CancellationToken ct = default);
    Task<PsBridgeResult> SaveAndCloseDocumentAsync(int documentId, string documentPath);
}
