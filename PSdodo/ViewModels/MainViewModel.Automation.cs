using PsExporter.Core.Automation;
using PsExporter.Models;
using PsExporter.Services;

namespace PsExporter.ViewModels;

public partial class MainViewModel
{
    internal string? CurrentPresetCommandId => CurrentPreset?.Id;

    internal bool TryStartExternalExport(ExportInvocation request)
    {
        if (IsBusy || IsPreparingExport)
        {
            AppendLog("Warn", "外部导出请求未接受：已有任务正在运行");
            return false;
        }

        ExportPresetParameters? parameters = null;
        if (request.Preset is { } reference)
        {
            var preset = request.Category is { } category
                ? _presets.Items.FirstOrDefault(item => item.Category == category &&
                    string.Equals(item.Name, reference, StringComparison.OrdinalIgnoreCase))
                : _presets.Items.FirstOrDefault(item => item.Id == reference);
            if (preset is null)
            {
                AppendLog("Warn", "外部导出请求未接受：找不到指定预设");
                return false;
            }

            parameters = ExportPresetCatalog.ParametersForPreset(preset);
        }

        _ = RunExternalExportAsync(parameters);
        return true;
    }

    private async Task RunExternalExportAsync(ExportPresetParameters? parameters)
    {
        try { await StartExportCoreAsync(parameters); }
        catch (Exception error)
        {
            ExportStatusText = "外部导出失败：" + error.Message;
            AppendLog("Error", "外部导出失败：" + error);
        }
    }
}
