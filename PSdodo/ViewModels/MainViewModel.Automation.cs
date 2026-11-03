using PsExporter.Core.Automation;
using PsExporter.Models;
using PsExporter.Services;

namespace PsExporter.ViewModels;

public partial class MainViewModel
{
    internal string? CurrentPresetCommandId => CurrentPreset?.Id;
    internal IReadOnlyList<ExportPreset> AutomationPresets => _presets.Items;
    internal event Action? AutomationPresetsChanged;
    public string AutomationPresetHint => CurrentPreset is { } preset
        ? $"使用「{preset.Name}」已保存的参数"
        : "先在导出页选择一份预设";

    internal static string? ExecutablePath => Environment.ProcessPath;

    /// <summary>两条复制入口（导出预设栏、导出指令窗口）都从这里取文案，保证同一份参数得到逐字节相同的指令。</summary>
    internal static string? BuildExportCommand(ExportInvocation invocation)
        => ExecutablePath is { } executable
            ? ExportInvocation.BuildWindowsCommandLine(executable, invocation)
            : null;

    internal static ExportInvocation InvocationForCurrentInterface() => new();
    internal static ExportInvocation InvocationForPreset(ExportPreset preset) => ExportInvocation.ForPresetId(preset.Id);

    private bool _commandUsePreset = true;
    private const int CommandWindowSizeVersion = 1;
    private Dictionary<string, string?> _commandPresets = [];
    private double _commandWidth, _commandHeight;

    public bool ExportCommandUsePreset => _commandUsePreset;
    public IReadOnlyDictionary<string, string?> ExportCommandPresets => _commandPresets;

    public (double Width, double Height) GetExportCommandWindowSize() => (
        SettingsService.NormalizeWindowDimension(_commandWidth, AppSettings.MinimumWindowWidth, AppSettings.MinimumWindowWidth),
        SettingsService.NormalizeWindowDimension(_commandHeight, AppSettings.MinimumCommandWindowHeight, AppSettings.MinimumCommandWindowHeight));

    internal void LoadExportCommandMemory(AppSettings settings)
    {
        _commandUsePreset = settings.ExportCommandUsePreset;
        _commandWidth = settings.ExportCommandWindowWidth;
        _commandHeight = settings.ExportCommandWindowHeight;
        // 旧版关窗时也会保存未调整过的默认尺寸，将这组尺寸迁移到紧凑版。
        if (settings.ExportCommandWindowSizeVersion < CommandWindowSizeVersion
            && _commandWidth == 440 && _commandHeight == 600)
        {
            _commandWidth = AppSettings.MinimumWindowWidth;
            _commandHeight = AppSettings.MinimumCommandWindowHeight;
        }
        _commandPresets = settings.ExportCommandPresets is { } stored
            ? new Dictionary<string, string?>(stored, StringComparer.Ordinal)
            : [];
    }

    internal AppSettings ExportCommandMemorySnapshot(AppSettings target)
    {
        target.ExportCommandUsePreset = _commandUsePreset;
        target.ExportCommandWindowWidth = _commandWidth;
        target.ExportCommandWindowHeight = _commandHeight;
        target.ExportCommandWindowSizeVersion = CommandWindowSizeVersion;
        target.ExportCommandPresets = new Dictionary<string, string?>(_commandPresets, StringComparer.Ordinal);
        return target;
    }

    /// <summary>窗口里的每一次手动选择都当场记账；不这样做，下次开窗又回到默认来源。</summary>
    internal void RememberExportCommandChoice(bool usePreset, ExportPresetCategory? category, string? presetId)
    {
        var changed = _commandUsePreset != usePreset;
        _commandUsePreset = usePreset;
        if (category is { } picked)
        {
            var key = picked.ToString();
            if (!_commandPresets.TryGetValue(key, out var stored) || stored != presetId)
            {
                _commandPresets[key] = presetId;
                changed = true;
            }
        }
        if (!changed) return;
        _settings.Save(CreateAppSettings());
    }

    internal void RememberExportCommandWindowSize(double width, double height)
    {
        if (Math.Abs(_commandWidth - width) < 0.5 && Math.Abs(_commandHeight - height) < 0.5) return;
        _commandWidth = width;
        _commandHeight = height;
        _settings.Save(CreateAppSettings());
    }

    internal bool TryStartExternalExport(ExportInvocation request)
    {
        if (IsBusy || IsPreparingExport)
        {
            AppendLog("Warn", "外部导出请求未接受：已有任务正在运行");
            return false;
        }

        ExportPresetParameters? parameters = null;
        if (request.Kind != ExportInvocationKind.CurrentInterface)
        {
            var preset = FindPresetForInvocation(request);
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

    private ExportPreset? FindPresetForInvocation(ExportInvocation request) => request.Kind switch
    {
        ExportInvocationKind.PresetById => _presets.Items.FirstOrDefault(item => item.Id == request.Preset),
        ExportInvocationKind.PresetByName when request.Category is { } category
            => _presets.Items.FirstOrDefault(item => item.Category == category &&
                string.Equals(item.Name, request.Preset, StringComparison.OrdinalIgnoreCase)),
        _ => null
    };

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
