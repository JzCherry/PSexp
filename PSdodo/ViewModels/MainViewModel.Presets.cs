using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PsExporter.Models;
using PsExporter.Services;

namespace PsExporter.ViewModels;

public sealed record ExportPresetListItem(string Id, string Name, string Summary, bool IsSelected)
{
    public string FormatSummary { get; init; } = "";
    public string Hint => (IsSelected ? "当前预设：" : "") + Name + " · " + Summary;
}

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
    [ObservableProperty] private string _presetEditorError = "";
    [ObservableProperty] private string _presetStatusMessage = "";

    public bool HasPresetStatus => !IsPresetEditorOpen && !string.IsNullOrEmpty(PresetStatusMessage);
    public bool HasPresetEditorError => !string.IsNullOrEmpty(PresetEditorError);
    public bool IsPresetDeleteEditor => _presetEditorAction == "delete";
    public bool IsPresetNameEditor => _presetEditorAction is "new" or "rename";
    public string PresetEditorTitle => _presetEditorAction switch
    {
        "rename" => "重命名预设", "delete" => "删除“" + (CurrentPreset?.Name ?? "预设") + "”？",
        _ => "新建预设"
    };
    public string PresetConfirmLabel => IsPresetDeleteEditor ? "删除" : "保存";
    public bool CanManagePresets => !IsBusy;
    public bool CanEditCurrentPreset => CanManagePresets && CurrentPreset is not null;
    public bool CanRestorePreset => CanManagePresets && CurrentPreset is not null && IsPresetModified;
    public bool CanSaveCurrentPreset => CanEditCurrentPreset && IsPresetModified && !IsPresetEditorOpen;
    public bool ShowPresetUpdate => IsPresetModified && !IsPresetEditorOpen;
    public bool HasCustomPresets => _presets.Items.Any(item => item.Category == _activePresetCategory);
    public bool HasCurrentPreset => CurrentPreset is not null;
    public string PresetStateText => !HasCurrentPreset ? "当前选项尚未保存为预设" : IsPresetModified ? "修改未保存" : "已保存";
    public string PresetDisplayName => CurrentPreset?.Name ?? (HasCustomPresets ? "选择预设" : "暂无预设");
    public bool IsPresetModified => _presetsInitialized && CurrentPreset is { } item && !ExportPresetCatalog.Matches(item, CapturePresetParameters(_activePresetMode));
    public string PresetPickerHint => !HasCustomPresets ? "暂无预设，点击新建保存当前选项"
        : PresetDisplayName + " · " + PresetStateText + "；选择后应用完整导出选项，不执行导出";
    private ExportPresetWorkspace CurrentPresetWorkspace => _presets.Workspaces.First(item => item.Category == _activePresetCategory);
    private ExportPreset? CurrentPreset => !_presetsInitialized ? null : FindPreset(CurrentPresetWorkspace.SelectedPresetId);
    private ExportPreset? FindPreset(string? id) => _presets.Items.FirstOrDefault(item => item.Category == _activePresetCategory && item.Id == id);

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
                    ImageName = category == ExportPresetCategory.Quick ? _quickImageName : category == ExportPresetCategory.Layers ? _layerImagePrefix : "",
                    FilePrefix = category switch { ExportPresetCategory.Layers => _layerImagePrefix, ExportPresetCategory.Artboards => _artboardImagePrefix, ExportPresetCategory.Slices => FileName, _ => null },
                    SlicePrefix = FileName, SliceFolder = SliceFolderName, ArtboardPath = ExportPathOption,
                    LayerRecursive = LayerExportRecursive, LayerSelectedOnly = LayerExportSelectedOnly,
                    LayerTopLevelSelectedOnly = LayerTopLevelSelectedOnly,
                    LayerScopeSelectedOnly = IsLayerSelectionSelectedOnly,
                    LayerWithDocumentName = false, UseDocumentName = false
                }
            });
        }
        InitializePresetDefinitions();
        _activePresetCategory = ExportPresetCatalog.CategoryFor(SelectedMode);
        _activePresetMode = SelectedMode;
        _presetsInitialized = true;
        RestorePresetWorkspace();
        RebuildPresetList();
        if (!string.IsNullOrEmpty(_presetStorage.Error)) PresetStatusMessage = _presetStorage.Error;
        SavePresetState();
    }

    private void InitializePresetDefinitions()
    {
        var migratedPartial = false;
        for (var index = 0; index < _presets.Items.Count; index++)
        {
            var item = _presets.Items[index];
            if (item.IncludeDestination && item.IncludeNaming) continue;
            var workspace = _presets.Workspaces.First(entry => entry.Category == item.Category);
            _presets.Items[index] = ExportPresetCatalog.CompleteLegacyPreset(item, workspace.Parameters);
            migratedPartial = true;
        }
        if (!_presets.DefaultsInitialized)
        {
            foreach (var category in Enum.GetValues<ExportPresetCategory>())
            {
                var item = ExportPresetCatalog.BuiltIn(category);
                if (_presets.Items.Any(entry => entry.Id == item.Id) || _presets.Items.Count >= ExportPresetCatalog.MaximumItems) continue;
                _presets.Items.Add(item with { Name = ExportPresetCatalog.UniqueName(item.Name, category, _presets.Items) });
            }
            _presets.DefaultsInitialized = true;
            _presetStateDirty = true;
        }
        if (migratedPartial || _presetStorage?.NeedsPrefixInputMigration == true || _presetStorage?.NeedsLayerScopeMigration == true) _presetStateDirty = true;
        if (_presetStateDirty) _presetStorage?.PreserveOriginalOnNextSave();
        if (migratedPartial) PresetStatusMessage = "旧预设未包含的位置与命名已补为对应板块当前设置。";
    }

    private ExportPresetParameters CapturePresetParameters(ExportMode mode) => new()
    {
        Mode = mode, Format = Format, Quality = Quality, AutoRename = AutoRename,
        Destination = Destination, CustomPath = ExportPath,
        ImageName = mode == ExportMode.QuickExport ? _quickImageName : mode == ExportMode.Layers ? _layerImagePrefix : "",
        FilePrefix = mode switch { ExportMode.Layers => _layerImagePrefix, ExportMode.AllArtboards or ExportMode.SelectedArtboards => _artboardImagePrefix, ExportMode.Slices => FileName, _ => null },
        SlicePrefix = FileName, SliceFolder = SliceFolderName, ArtboardPath = ExportPathOption,
        LayerRecursive = LayerExportRecursive, LayerSelectedOnly = LayerExportSelectedOnly,
        LayerTopLevelSelectedOnly = LayerTopLevelSelectedOnly,
        LayerScopeSelectedOnly = IsLayerSelectionSelectedOnly,
        LayerWithDocumentName = false, UseDocumentName = false
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
                LayerTopLevelSelectedOnly = p.LayerTopLevelSelectedOnly;
            }
            if (_activePresetCategory == ExportPresetCategory.Slices)
            {
                SliceFolderName = p.SliceFolder;
                if (naming) FileName = p.SlicePrefix;
            }
            if (naming) ImageName = _activePresetCategory == ExportPresetCategory.Quick ? p.ImageName : p.FilePrefix ?? "";
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
        foreach (var item in _presets.Items.Where(item => item.Category == _activePresetCategory))
        {
            var p = item.Parameters;
            var formatSummary = p.Format == ExportFormat.Png ? "PNG" : $"JPG · {p.Quality}%";
            var summary = formatSummary
                + (item.Category == ExportPresetCategory.Artboards ? p.Mode == ExportMode.SelectedArtboards ? " · 选中画板" : " · 全部画板" : "")
                + (item.Category == ExportPresetCategory.Layers
                    ? (p.LayerRecursive ? " · 图层素材" : " · 画布合成") + (p.LayerScopeSelectedOnly == true ? " · 选中" : " · 全部") : "");
            AvailablePresets.Add(new(item.Id, item.Name, summary, item.Id == CurrentPresetWorkspace.SelectedPresetId) { FormatSummary = formatSummary });
        }
        NotifyPresetProperties();
    }

    private void NotifyPresetProperties()
    {
        foreach (var name in new[] { nameof(PresetDisplayName), nameof(AutomationPresetHint), nameof(IsPresetModified), nameof(PresetStateText), nameof(HasCurrentPreset), nameof(ShowPresetUpdate), nameof(CanEditCurrentPreset), nameof(CanRestorePreset), nameof(CanSaveCurrentPreset), nameof(HasCustomPresets), nameof(PresetPickerHint), nameof(CanManagePresets) }) OnPropertyChanged(name);
        for (var index = 0; index < AvailablePresets.Count; index++)
        {
            var item = AvailablePresets[index];
            var selected = item.Id == CurrentPresetWorkspace.SelectedPresetId;
            if (item.IsSelected != selected) AvailablePresets[index] = item with { IsSelected = selected };
        }
        ApplyPresetCommand.NotifyCanExecuteChanged(); SaveCurrentPresetCommand.NotifyCanExecuteChanged();
        SaveNewPresetCommand.NotifyCanExecuteChanged(); RestorePresetCommand.NotifyCanExecuteChanged();
        RenamePresetCommand.NotifyCanExecuteChanged(); DeletePresetCommand.NotifyCanExecuteChanged();
        CopyPresetCommand.NotifyCanExecuteChanged(); ConfirmPresetEditorCommand.NotifyCanExecuteChanged();
    }

    private static readonly HashSet<string> PresetParameterProperties = new(StringComparer.Ordinal)
    {
        nameof(SelectedMode), nameof(Format), nameof(Quality), nameof(AutoRename), nameof(Destination), nameof(ExportPath),
        nameof(ImageName), nameof(FileName), nameof(SliceFolderName), nameof(ExportPathOption), nameof(LayerExportRecursive),
        nameof(LayerExportSelectedOnly), nameof(LayerTopLevelSelectedOnly)
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
        ApplyPresetDefinition(preset);
    }

    private void ApplyPresetDefinition(ExportPreset preset)
    {
        CurrentPresetWorkspace.SelectedPresetId = preset.Id;
        ApplyPresetParameters(preset.Parameters, true, true);
        _presetStateDirty = true;
        CancelPresetEditor(); NotifyPresetProperties();
        PresetStatusMessage = "";
        SaveBehaviorSettings();
    }

    [RelayCommand(CanExecute = nameof(CanSaveCurrentPreset))]
    private void SaveCurrentPreset() => UpdateSelectedPreset();

    private bool UpdateSelectedPreset()
    {
        if (!CanEditCurrentPreset || CurrentPreset is not { } current) return false;
        var index = _presets.Items.FindIndex(item => item.Id == current.Id);
        var updated = current with { IncludeDestination = true, IncludeNaming = true, Parameters = CapturePresetParameters(SelectedMode) };
        _presets.Items[index] = updated with { Parameters = ExportPresetCatalog.ParametersForPreset(updated) };
        if (FinishPresetMutation("已更新“" + current.Name + "”。")) return true;
        _presets.Items[index] = current;
        _presetStateDirty = true;
        RebuildPresetList();
        return false;
    }

    [RelayCommand(CanExecute = nameof(CanManagePresets))]
    private void SaveNewPreset() => OpenPresetEditor("new", ExportPresetCatalog.UniqueName("自定义预设", _activePresetCategory, _presets.Items));

    [RelayCommand(CanExecute = nameof(CanEditCurrentPreset))]
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
        foreach (var property in new[] { nameof(PresetEditorTitle), nameof(IsPresetDeleteEditor), nameof(IsPresetNameEditor), nameof(PresetConfirmLabel) }) OnPropertyChanged(property);
        IsPresetEditorOpen = true;
    }
    [RelayCommand]
    private void CancelPresetEditor() { IsPresetEditorOpen = false; PresetEditorError = ""; }
    partial void OnIsPresetEditorOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(HasPresetStatus));
        NotifyPresetProperties();
    }
    partial void OnPresetStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasPresetStatus));
    partial void OnPresetEditorErrorChanged(string value) => OnPropertyChanged(nameof(HasPresetEditorError));

    [RelayCommand(CanExecute = nameof(CanManagePresets))]
    private void ConfirmPresetEditor()
    {
        if (!IsPresetEditorOpen) return;
        var target = _presets.Items.FirstOrDefault(item => item.Id == _presetEditorTarget && item.Category == _activePresetCategory);
        var originalItems = _presets.Items.ToList();
        var originalSelection = CurrentPresetWorkspace.SelectedPresetId;
        void CompleteMutation(string message)
        {
            if (FinishPresetMutation(message)) { CancelPresetEditor(); return; }
            _presets.Items = originalItems;
            CurrentPresetWorkspace.SelectedPresetId = originalSelection;
            _presetStateDirty = true;
            RebuildPresetList();
            PresetEditorError = string.IsNullOrEmpty(PresetStatusMessage) ? "保存失败，请重试。" : PresetStatusMessage;
        }
        if (_presetEditorAction == "delete")
        {
            if (target is null) { PresetEditorError = "这个预设已不存在。"; return; }
            _presets.Items.Remove(target); CurrentPresetWorkspace.SelectedPresetId = null;
            CompleteMutation("预设已删除，当前导出参数保留。"); return;
        }
        var name = (PresetEditorName ?? "").Trim();
        if (name.Length == 0 || name.Length > ExportPresetCatalog.MaximumNameLength || name.Any(char.IsControl)) { PresetEditorError = "请输入 1–40 个字符的预设名称。"; return; }
        if (_presets.Items.Any(item => item.Category == _activePresetCategory && item.Id != (_presetEditorAction == "rename" ? target?.Id : null) && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
        { PresetEditorError = "此板块已有同名预设，请换一个名称。"; return; }
        if (_presetEditorAction == "rename")
        {
            if (target is null) { PresetEditorError = "这个预设已不存在。"; return; }
            _presets.Items[_presets.Items.IndexOf(target)] = target with { Name = name };
        }
        else
        {
            if (_presets.Items.Count >= ExportPresetCatalog.MaximumItems) { PresetEditorError = "最多保存 200 个预设。"; return; }
            var item = new ExportPreset { Id = Guid.NewGuid().ToString("N"), Name = name, Category = _activePresetCategory,
                IncludeDestination = true, IncludeNaming = true, Parameters = CapturePresetParameters(SelectedMode) };
            item = item with { Parameters = ExportPresetCatalog.ParametersForPreset(item) };
            _presets.Items.Add(item); CurrentPresetWorkspace.SelectedPresetId = item.Id;
        }
        CompleteMutation("已保存“" + name + "”。");
    }

    private bool FinishPresetMutation(string message)
    {
        _presetStateDirty = true; RebuildPresetList();
        if (!SavePresetState()) return false;
        SaveBehaviorSettings();
        PresetStatusMessage = ""; AppendLog("Info", message);
        return true;
    }

    public void ImportPresets(string json)
    {
        if (!CanManagePresets) return;
        try
        {
            var imported = ExportPresetCatalog.ParseImport(json);
            if (_presets.Items.Count + imported.Count > ExportPresetCatalog.MaximumItems) throw new InvalidDataException("导入后超过 200 个预设，未导入任何内容。");
            foreach (var item in imported)
            {
                var workspace = _presets.Workspaces.First(entry => entry.Category == item.Category);
                var complete = ExportPresetCatalog.CompleteLegacyPreset(item, workspace.Parameters);
                _presets.Items.Add(complete with { Name = ExportPresetCatalog.UniqueName(complete.Name, complete.Category, _presets.Items) });
            }
            FinishPresetMutation($"已导入 {imported.Count} 个预设，按各自板块归类。");
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidDataException or ArgumentException)
        { ReportPresetError(error.Message); }
    }
    public string ExportCurrentPresets() => ExportPresetCatalog.Export(_presets.Items.Where(item => item.Category == _activePresetCategory));
    public void ReportPresetError(string message) => PresetStatusMessage = message;
}
