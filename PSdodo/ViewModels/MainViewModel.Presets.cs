using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PsExporter.Models;
using PsExporter.Services;

namespace PsExporter.ViewModels;

public sealed record ExportPresetListItem(string Id, string Name, string Summary, bool IsBuiltIn);

public partial class MainViewModel
{
    private ExportPresetsStorage? _presetStorage;
    private ExportPresetsState _presets = new();
    private ExportPresetCategory _activePresetCategory;
    private ExportMode _activePresetMode;
    private bool _presetsInitialized, _applyingPreset, _presetStateDirty;
    private string _presetEditorAction = "";
    private string? _presetEditorTarget;
    public ObservableCollection<ExportPresetListItem> AvailablePresets { get; } = [];
    [ObservableProperty] private bool _isPresetEditorOpen;
    [ObservableProperty] private string _presetEditorName = "";
    [ObservableProperty] private bool _presetIncludeDestination = true;
    [ObservableProperty] private bool _presetIncludeNaming = true;
    [ObservableProperty] private string _presetEditorError = "";
    [ObservableProperty] private string _presetStatusMessage = "";

    public bool HasPresetStatus => !string.IsNullOrEmpty(PresetStatusMessage);
    public bool HasPresetEditorError => !string.IsNullOrEmpty(PresetEditorError);
    public bool IsPresetDeleteEditor => _presetEditorAction == "delete";
    public bool IsPresetSaveEditor => _presetEditorAction == "new";
    public bool IsPresetNameEditor => !IsPresetDeleteEditor;
    public string PresetEditorTitle => _presetEditorAction switch { "rename" => "重命名预设", "delete" => "删除这个预设？", _ => "保存当前" + ExportPresetCatalog.CategoryName(_activePresetCategory) + "选项" };
    public string PresetConfirmLabel => IsPresetDeleteEditor ? "删除" : "保存";
    public bool CanManagePresets => !IsBusy;
    public bool CanEditCurrentPreset => CanManagePresets && CurrentPreset is { } item && !ExportPresetCatalog.IsBuiltIn(item.Id);
    public bool CanRestorePreset => CanManagePresets && CurrentPreset is not null && IsPresetModified;
    public bool CanSaveCurrentPreset => CanManagePresets && (!CanEditCurrentPreset || IsPresetModified);
    public bool HasCustomPresets => _presets.Items.Any(item => item.Category == _activePresetCategory);
    public string PresetSaveLabel => CurrentPreset is { } item && !ExportPresetCatalog.IsBuiltIn(item.Id) ? "更新" : "保存";
    public string PresetDisplayName => CurrentPreset?.Name ?? "当前设置";
    public bool IsPresetModified => _presetsInitialized && CurrentPreset is { } item && !ExportPresetCatalog.Matches(item, CapturePresetParameters(_activePresetMode));
    public string PresetPickerHint => PresetDisplayName + " · " + ExportPresetCatalog.CategoryName(_activePresetCategory) + "；选择只恢复参数，不执行导出";
    private ExportPresetWorkspace CurrentPresetWorkspace => _presets.Workspaces.First(item => item.Category == _activePresetCategory);
    private ExportPreset? CurrentPreset => !_presetsInitialized ? null : FindPreset(CurrentPresetWorkspace.SelectedPresetId);
    private ExportPreset? FindPreset(string? id) => id == ExportPresetCatalog.BuiltIn(_activePresetCategory).Id
        ? ExportPresetCatalog.BuiltIn(_activePresetCategory) : _presets.Items.FirstOrDefault(item => item.Category == _activePresetCategory && item.Id == id);

    private void InitializePresets(AppSettings legacy)
    {
        _presetStorage = new ExportPresetsStorage(_settings.DataDirectory);
        _presets = _presetStorage.Load();
        foreach (var category in Enum.GetValues<ExportPresetCategory>())
        {
            if (_presets.Workspaces.Any(item => item.Category == category)) continue;
            _presets.Workspaces.Add(new()
            {
                Category = category,
                Parameters = new()
                {
                    Mode = ExportPresetCatalog.CategoryFor(legacy.LastExportMode) == category ? legacy.LastExportMode : ExportPresetCatalog.DefaultMode(category),
                    Format = Format, Quality = Quality, AutoRename = AutoRename, Destination = Destination, CustomPath = ExportPath,
                    ImageName = category == ExportPresetCategory.Quick ? _quickImageName : _layerImagePrefix,
                    SlicePrefix = FileName, SliceFolder = SliceFolderName, ArtboardPath = ExportPathOption,
                    LayerRecursive = LayerExportRecursive, LayerSelectedOnly = LayerExportSelectedOnly,
                    LayerWithDocumentName = LayerExportWithDocName, UseDocumentName = AutoUseDocNameAsPrefix
                }
            });
        }
        _activePresetCategory = ExportPresetCatalog.CategoryFor(SelectedMode);
        _activePresetMode = SelectedMode;
        _presetsInitialized = true;
        RestorePresetWorkspace();
        RebuildPresetList();
        if (!string.IsNullOrEmpty(_presetStorage.Error)) PresetStatusMessage = _presetStorage.Error;
    }

    private ExportPresetParameters CapturePresetParameters(ExportMode mode) => new()
    {
        Mode = mode, Format = Format, Quality = Quality, AutoRename = AutoRename,
        Destination = Destination, CustomPath = ExportPath,
        ImageName = mode == ExportMode.QuickExport ? _quickImageName : _layerImagePrefix,
        SlicePrefix = FileName, SliceFolder = SliceFolderName, ArtboardPath = ExportPathOption,
        LayerRecursive = LayerExportRecursive, LayerSelectedOnly = LayerExportSelectedOnly,
        LayerWithDocumentName = LayerExportWithDocName, UseDocumentName = AutoUseDocNameAsPrefix
    };

    private void RememberPresetWorkspace()
    {
        if (!_presetsInitialized || _applyingPreset) return;
        var current = CapturePresetParameters(_activePresetMode);
        if (CurrentPresetWorkspace.Parameters == current) return;
        CurrentPresetWorkspace.Parameters = current;
        _presetStateDirty = true;
    }

    private void SwitchPresetWorkspace(ExportMode requested)
    {
        if (!_presetsInitialized || _applyingPreset) return;
        var category = ExportPresetCatalog.CategoryFor(requested);
        if (category == _activePresetCategory) { _activePresetMode = requested; return; }
        RememberPresetWorkspace();
        _activePresetCategory = category;
        RestorePresetWorkspace();
        CancelPresetEditor();
        PresetStatusMessage = "";
        RebuildPresetList();
    }

    private void RestorePresetWorkspace()
    {
        ApplyPresetParameters(CurrentPresetWorkspace.Parameters, true, true, restoreSession: true);
        _activePresetMode = SelectedMode;
    }

    private void ApplyPresetParameters(ExportPresetParameters value, bool destination, bool naming, bool restoreSession = false)
    {
        var p = ExportPresetCatalog.Normalize(value, _activePresetCategory);
        _applyingPreset = true;
        try
        {
            SelectedMode = p.Mode;
            Format = p.Format; Quality = p.Quality; AutoRename = p.AutoRename;
            if (destination)
            {
                Destination = p.Destination;
                if (restoreSession || p.Destination == ExportDestination.Custom) ExportPath = p.CustomPath;
            }
            if (_activePresetCategory == ExportPresetCategory.Artboards) ExportPathOption = p.ArtboardPath;
            if (_activePresetCategory == ExportPresetCategory.Layers)
            {
                LayerExportRecursive = p.LayerRecursive;
                LayerExportSelectedOnly = p.LayerSelectedOnly;
                if (naming) { LayerExportWithDocName = p.LayerWithDocumentName; AutoUseDocNameAsPrefix = p.UseDocumentName; }
            }
            if (_activePresetCategory == ExportPresetCategory.Slices)
            {
                SliceFolderName = p.SliceFolder;
                if (naming) FileName = p.SlicePrefix;
            }
            if (naming && _activePresetCategory is ExportPresetCategory.Quick or ExportPresetCategory.Layers) ImageName = p.ImageName;
        }
        finally { _applyingPreset = false; }
        _activePresetMode = SelectedMode;
        RememberPresetWorkspace();
    }

    private bool SavePresetState()
    {
        if (!_presetsInitialized || _applyingPreset || _presetStorage is null) return true;
        RememberPresetWorkspace();
        if (!_presetStateDirty) return true;
        if (!_presetStorage.Save(_presets)) { PresetStatusMessage = _presetStorage.Error; return false; }
        _presetStateDirty = false;
        return true;
    }

    private void RebuildPresetList()
    {
        AvailablePresets.Clear();
        foreach (var item in new[] { ExportPresetCatalog.BuiltIn(_activePresetCategory) }.Concat(_presets.Items.Where(item => item.Category == _activePresetCategory)))
        {
            var p = item.Parameters;
            var summary = (p.Format == ExportFormat.Png ? "PNG" : $"JPG · {p.Quality}%")
                + (item.Category == ExportPresetCategory.Artboards ? p.Mode == ExportMode.SelectedArtboards ? " · 选中画板" : " · 全部画板" : "")
                + (item.Category == ExportPresetCategory.Layers ? p.LayerRecursive ? p.LayerSelectedOnly ? " · 选中图层" : " · 全部图层" : " · 一级图层" : "");
            AvailablePresets.Add(new(item.Id, item.Name, summary, ExportPresetCatalog.IsBuiltIn(item.Id)));
        }
        NotifyPresetProperties();
    }

    private void NotifyPresetProperties()
    {
        foreach (var name in new[] { nameof(PresetDisplayName), nameof(IsPresetModified), nameof(PresetSaveLabel), nameof(CanEditCurrentPreset), nameof(CanRestorePreset), nameof(CanSaveCurrentPreset), nameof(HasCustomPresets), nameof(PresetPickerHint), nameof(CanManagePresets) }) OnPropertyChanged(name);
        ApplyPresetCommand.NotifyCanExecuteChanged(); SaveCurrentPresetCommand.NotifyCanExecuteChanged();
        SaveNewPresetCommand.NotifyCanExecuteChanged(); RestorePresetCommand.NotifyCanExecuteChanged();
        RenamePresetCommand.NotifyCanExecuteChanged(); DeletePresetCommand.NotifyCanExecuteChanged();
        CopyPresetCommand.NotifyCanExecuteChanged(); ConfirmPresetEditorCommand.NotifyCanExecuteChanged();
    }

    private static readonly HashSet<string> PresetParameterProperties = new(StringComparer.Ordinal)
    {
        nameof(SelectedMode), nameof(Format), nameof(Quality), nameof(AutoRename), nameof(Destination), nameof(ExportPath),
        nameof(ImageName), nameof(FileName), nameof(SliceFolderName), nameof(ExportPathOption), nameof(LayerExportRecursive),
        nameof(LayerExportSelectedOnly), nameof(LayerExportWithDocName), nameof(AutoUseDocNameAsPrefix)
    };
    protected override void OnPropertyChanged(PropertyChangedEventArgs args)
    {
        base.OnPropertyChanged(args);
        if (!_presetsInitialized || _applyingPreset) return;
        if (args.PropertyName is { } name && PresetParameterProperties.Contains(name)) { RememberPresetWorkspace(); NotifyPresetProperties(); }
        else if (args.PropertyName == nameof(IsBusy)) NotifyPresetProperties();
    }

    [RelayCommand(CanExecute = nameof(CanManagePresets))]
    private void ApplyPreset(ExportPresetListItem? choice)
    {
        if (choice is null || FindPreset(choice.Id) is not { } preset) return;
        CurrentPresetWorkspace.SelectedPresetId = preset.Id;
        ApplyPresetParameters(preset.Parameters, preset.IncludeDestination, preset.IncludeNaming);
        _presetStateDirty = true;
        CancelPresetEditor(); NotifyPresetProperties();
        if (SaveBehaviorSettings()) PresetStatusMessage = "已应用“" + preset.Name + "”，点击导出后才执行。";
    }

    [RelayCommand(CanExecute = nameof(CanSaveCurrentPreset))]
    private void SaveCurrentPreset()
    {
        if (!CanEditCurrentPreset) { SaveNewPreset(); return; }
        var current = CurrentPreset!;
        var updated = current with { Parameters = CapturePresetParameters(SelectedMode) };
        _presets.Items[_presets.Items.FindIndex(item => item.Id == current.Id)] = updated with { Parameters = ExportPresetCatalog.ParametersForPreset(updated) };
        FinishPresetMutation("已更新“" + current.Name + "”。");
    }

    [RelayCommand(CanExecute = nameof(CanManagePresets))]
    private void SaveNewPreset() => OpenPresetEditor("new", ExportPresetCatalog.UniqueName("自定义预设", _activePresetCategory, _presets.Items));

    [RelayCommand(CanExecute = nameof(CanManagePresets))]
    private void CopyPreset()
    {
        var name = PresetDisplayName + " 副本";
        OpenPresetEditor("new", ExportPresetCatalog.UniqueName(name[..Math.Min(name.Length, ExportPresetCatalog.MaximumNameLength)], _activePresetCategory, _presets.Items));
    }
    [RelayCommand(CanExecute = nameof(CanEditCurrentPreset))]
    private void RenamePreset() => OpenPresetEditor("rename", CurrentPreset!.Name);
    [RelayCommand(CanExecute = nameof(CanEditCurrentPreset))]
    private void DeletePreset() => OpenPresetEditor("delete", CurrentPreset!.Name);
    [RelayCommand(CanExecute = nameof(CanRestorePreset))]
    private void RestorePreset()
    {
        if (CurrentPreset is not { } current) return;
        ApplyPresetParameters(current.Parameters, current.IncludeDestination, current.IncludeNaming);
        NotifyPresetProperties();
        if (SaveBehaviorSettings()) PresetStatusMessage = "已恢复预设中的参数。";
    }

    private void OpenPresetEditor(string action, string name)
    {
        _presetEditorAction = action; _presetEditorTarget = CurrentPreset?.Id;
        PresetEditorName = name; PresetEditorError = ""; PresetStatusMessage = "";
        PresetIncludeDestination = CurrentPreset?.IncludeDestination ?? true;
        PresetIncludeNaming = CurrentPreset?.IncludeNaming ?? true;
        foreach (var property in new[] { nameof(PresetEditorTitle), nameof(IsPresetDeleteEditor), nameof(IsPresetSaveEditor), nameof(IsPresetNameEditor), nameof(PresetConfirmLabel) }) OnPropertyChanged(property);
        IsPresetEditorOpen = true;
    }
    [RelayCommand]
    private void CancelPresetEditor() { IsPresetEditorOpen = false; PresetEditorError = ""; }
    [RelayCommand] private void SetPresetIncludeDestination(object? value) => PresetIncludeDestination = ParseBooleanCommandParameter(value);
    [RelayCommand] private void SetPresetIncludeNaming(object? value) => PresetIncludeNaming = ParseBooleanCommandParameter(value);
    partial void OnPresetStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasPresetStatus));
    partial void OnPresetEditorErrorChanged(string value) => OnPropertyChanged(nameof(HasPresetEditorError));

    [RelayCommand(CanExecute = nameof(CanManagePresets))]
    private void ConfirmPresetEditor()
    {
        if (!IsPresetEditorOpen) return;
        var target = _presets.Items.FirstOrDefault(item => item.Id == _presetEditorTarget && item.Category == _activePresetCategory);
        if (_presetEditorAction == "delete")
        {
            if (target is null) { PresetEditorError = "这个预设已不存在。"; return; }
            _presets.Items.Remove(target); CurrentPresetWorkspace.SelectedPresetId = null;
            CancelPresetEditor(); FinishPresetMutation("预设已删除，当前导出参数保留。"); return;
        }
        var name = (PresetEditorName ?? "").Trim();
        if (name.Length == 0 || name.Length > ExportPresetCatalog.MaximumNameLength || name.Any(char.IsControl)) { PresetEditorError = "请输入 1–40 个字符的预设名称。"; return; }
        if (name == "默认预设" || _presets.Items.Any(item => item.Category == _activePresetCategory && item.Id != (_presetEditorAction == "rename" ? target?.Id : null) && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
        { PresetEditorError = "此板块已有同名预设，请换一个名称。"; return; }
        if (_presetEditorAction == "rename")
        {
            if (target is null) { PresetEditorError = "这个预设已不存在。"; return; }
            _presets.Items[_presets.Items.IndexOf(target)] = target with { Name = name };
        }
        else
        {
            if (_presets.Items.Count >= ExportPresetCatalog.MaximumItems) { PresetEditorError = "最多保存 200 个自定义预设。"; return; }
            var item = new ExportPreset { Id = Guid.NewGuid().ToString("N"), Name = name, Category = _activePresetCategory,
                IncludeDestination = PresetIncludeDestination, IncludeNaming = PresetIncludeNaming, Parameters = CapturePresetParameters(SelectedMode) };
            item = item with { Parameters = ExportPresetCatalog.ParametersForPreset(item) };
            _presets.Items.Add(item); CurrentPresetWorkspace.SelectedPresetId = item.Id;
        }
        CancelPresetEditor(); FinishPresetMutation("已保存“" + name + "”。");
    }

    private void FinishPresetMutation(string message)
    {
        _presetStateDirty = true; RebuildPresetList();
        if (SaveBehaviorSettings()) { PresetStatusMessage = message; AppendLog("Info", message); }
    }

    public void ImportPresets(string json)
    {
        if (!CanManagePresets) return;
        try
        {
            var imported = ExportPresetCatalog.ParseImport(json);
            if (_presets.Items.Count + imported.Count > ExportPresetCatalog.MaximumItems) throw new InvalidDataException("导入后超过 200 个预设，未导入任何内容。");
            foreach (var item in imported)
                _presets.Items.Add(item with { Name = ExportPresetCatalog.UniqueName(item.Name, item.Category, _presets.Items.Append(ExportPresetCatalog.BuiltIn(item.Category))) });
            FinishPresetMutation($"已导入 {imported.Count} 个预设，按各自板块归类。");
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidDataException or ArgumentException)
        { ReportPresetError(error.Message); }
    }
    public string ExportCurrentPresets() => ExportPresetCatalog.Export(_presets.Items.Where(item => item.Category == _activePresetCategory));
    public void ReportPresetError(string message) => PresetStatusMessage = message;
}
