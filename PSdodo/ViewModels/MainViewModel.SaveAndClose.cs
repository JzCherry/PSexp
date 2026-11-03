using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PsExporter.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveAndCloseHint))]
    private bool _saveAndCloseAfterExport;
    private bool _isSavingExportDocument;

    public string SaveAndCloseHint =>
        (SaveAndCloseAfterExport ? "已开启。" : "已关闭。")
        + "导出成功后，将原文档保存到已有的本地文件并关闭。未保存或云端文档会保持打开。";

    partial void OnSaveAndCloseAfterExportChanged(bool value) => SaveBehaviorSettings();

    [RelayCommand]
    private void ToggleSaveAndCloseAfterExport()
    {
        if (!IsBusy) SaveAndCloseAfterExport = !SaveAndCloseAfterExport;
    }

    private async Task SaveAndCloseExportedDocumentAsync(string receipt, int processId)
    {
        var completedStatus = ExportStatusText;
        try
        {
            using var json = JsonDocument.Parse(receipt);
            if (!json.RootElement.TryGetProperty("sourceDocument", out var source)
                || source.ValueKind != JsonValueKind.Object
                || !source.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var documentId) || documentId <= 0)
                throw new IOException("无法确认原导出文档");
            var path = source.TryGetProperty("path", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path))
                throw new IOException("原文档没有可用的本地保存路径");
            if (processId <= 0 || _ps.DetectProcess().ProcessId != processId)
                throw new IOException("Photoshop 实例已变化");

            _isSavingExportDocument = true;
            RefreshCanExecute();
            ExportStatusText = "图片已导出，正在保存并关闭原文档…";
            var result = await _export.SaveAndCloseDocumentAsync(documentId, path);
            if (!result.Success) throw result.Error ?? new IOException("Photoshop 未确认操作完成");
            AppendLog("Info", "原导出文档已保存并关闭：" + path);
            await RefreshStatusCoreAsync(silent: true, quiet: true);
            ExportStatusText = completedStatus + "。原文档已保存并关闭。";
        }
        catch (Exception error)
        {
            var reason = error is JsonException ? "无法确认原导出文档" : error.Message;
            ExportStatusText = completedStatus + "。保存并关闭未完成：" + reason;
            AppendLog("Warn", "保存并关闭未完成：" + error);
        }
    }
}
