using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PsExporter.Core.Logging;
using PsExporter.Core.PsConnection;
using PsExporter.Models;
using PsExporter.Services;

namespace PsExporter.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly IPhotoshopService _ps;
    private readonly IExportService _export;
    private readonly ISettingsService _settings;
    private readonly FileLogger _fileLogger;
    private CancellationTokenSource? _exportCts;
    private bool _settingsInitialized;
    private bool _suppressNextActivationRefresh;
    private int _documentProcessId;
    private int? _activeExpectedFileCount;
    private double _windowWidth = AppSettings.DefaultWindowWidth;
    private double _windowHeight = AppSettings.DefaultWindowHeight;

    public MainViewModel(IPhotoshopService ps, IExportService export, ISettingsService settings, FileLogger fileLogger)
    {
        _ps = ps;
        _export = export;
        _settings = settings;
        _fileLogger = fileLogger;

        var s = _settings.Load();
        ExportPath = s.DefaultExportPath;
        Destination = s.DefaultDestination is { } savedDestination && Enum.IsDefined(savedDestination)
            ? savedDestination
            : string.IsNullOrWhiteSpace(s.DefaultExportPath) ? ExportDestination.DocumentFolder : ExportDestination.Custom;
        _format = Enum.IsDefined(s.DefaultFormat) ? s.DefaultFormat : ExportFormat.Jpg;
        _isJpgFormat = _format == ExportFormat.Jpg;
        _quickImageName = s.QuickImageName ?? string.Empty;
        _layerImagePrefix = s.LayerImagePrefix ?? string.Empty;
        _selectedMode = Enum.IsDefined(s.LastExportMode) ? s.LastExportMode : ExportMode.QuickExport;
        _exportPathOption = Enum.IsDefined(s.LastArtboardPathOption) ? s.LastArtboardPathOption : ExportPathOption.ImageOnly;
        _imageName = _selectedMode == ExportMode.Layers ? _layerImagePrefix : _quickImageName;
        _sliceFolderName = s.SliceFolderName ?? string.Empty;
        _selectedSettingsSection = Math.Clamp(s.LastSettingsSection, 0, 3);
        _isViewSetting = s.ShowSettingsOnStartup;
        Quality = s.DefaultQuality;
        AutoRename = s.DefaultAutoRename;
        FileName = s.SliceFilePrefix ?? string.Empty;
        LayerExportWithDocName = s.LayerExportWithDocName;
        LayerExportRecursive = s.LayerExportRecursive;
        LayerExportSelectedOnly = s.LayerExportSelectedOnly;
        LayerExportShowHint = s.LayerExportShowHint;
        AutoCheckForUpdates = s.AutoCheckForUpdates;
        AutoDownloadUpdates = s.AutoDownloadUpdates;
        CloseToTray = s.CloseToTray;
        EnableBlankDrag = s.EnableBlankDrag;
        EnableQualityHoverAnimation = s.EnableQualityHoverAnimation;
        DockRetractDelayMs = s.DockRetractDelayMs is 250 or 420 or 800 ? s.DockRetractDelayMs : 420;
        AutoUseDocNameAsPrefix = s.AutoUseDocNameAsPrefix;
        IsAlwaysOnTop = s.StartAlwaysOnTop;
        AutoRefreshOnActivated = s.AutoRefreshOnActivated;
        EnableEdgeSnap = s.EnableEdgeSnap;
        ThemeMode = s.ThemeMode;
        _windowPlacement = s.WindowPlacement;
        _windowWidth = s.WindowWidth;
        _windowHeight = s.WindowHeight;
        App.ApplyTheme(ThemeMode);
        _settingsInitialized = true;

        Artboards = new ObservableCollection<ArtboardSelectionViewModel>();
        Logs = new ObservableCollection<LogLineViewModel>();
        ExportedFiles = new ObservableCollection<ExportedFileViewModel>();
        AvailableFormats = Enum.GetValues<ExportFormat>();
        AvailableModes = Enum.GetValues<ExportMode>();

        InitializePresets(s);
        InitializeDocumentEvents();
        // 构造时只准备事件接收器；首次前台成功识别后才注册 Photoshop 事件。
    }

    // ---------- PS 状态 ----------
    [ObservableProperty]
    private bool _isPsRunning;

    [ObservableProperty]
    private string _psVersion = string.Empty;

    [ObservableProperty]
    private string _psExecutablePath = string.Empty;

    [ObservableProperty]
    private bool _isBridgeAvailable;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isRefreshingStatus;

    public bool CanRefreshStatus => !IsBusy && !IsRefreshingStatus;

    [ObservableProperty]
    private bool _isAlwaysOnTop;

    [ObservableProperty]
    private bool _autoRefreshOnActivated = true;

    [ObservableProperty]
    private bool _enableEdgeSnap = true;

    /// <summary>窗口当前处于贴边收纳状态；收纳期间强制置顶，避免感应条被其他窗口遮挡。</summary>
    [ObservableProperty]
    private bool _isEdgeDocked;

    public bool EffectiveTopmost => CalculateEffectiveTopmost(IsAlwaysOnTop, IsExporting, IsEdgeDocked);

    public static bool CalculateEffectiveTopmost(bool isAlwaysOnTop, bool isExporting, bool isEdgeDocked)
        => isAlwaysOnTop || isExporting || isEdgeDocked;

    // ---------- 导出进度 ----------
    [ObservableProperty]
    private bool _isExporting;

    [ObservableProperty]
    private bool _isCancelling;

    [ObservableProperty]
    private string _exportTaskName = string.Empty;

    [ObservableProperty]
    private int _exportedCount;

    [ObservableProperty]
    private string _lastExportedFile = string.Empty;

    [ObservableProperty]
    private string _exportStatusText = "尚未执行任务";

    [ObservableProperty]
    private bool _canOpenExportFolder;

    private string _lastSuccessfulExportDirectory = string.Empty;

    // ---------- 文档信息 ----------
    [ObservableProperty]
    private string _documentName = string.Empty;

    [ObservableProperty]
    private bool _hasDocument;

    [ObservableProperty]
    private bool _hasArtboards;

    [ObservableProperty]
    private int _artboardCount;

    [ObservableProperty]
    private bool _hasSlices;

    [ObservableProperty]
    private string _documentSize = string.Empty;

    // ---------- 导出参数 ----------
    [ObservableProperty]
    private string _exportPath = string.Empty;

    [ObservableProperty]
    private ExportFormat _format = ExportFormat.Jpg;

    /// <summary>当前格式是否为 JPG（用于 UI 控制 JPG 质量行的显示）</summary>
    [ObservableProperty]
    private bool _isJpgFormat = true;

    [ObservableProperty]
    private int _quality = 80;

    [ObservableProperty]
    private bool _autoRename = true;

    [ObservableProperty]
    private string _fileName = string.Empty;

    /// <summary>图片命名（通用前缀，支持所有导出模式）</summary>
    [ObservableProperty]
    private string _imageName = string.Empty;

    /// <summary>画板导出路径选项（仅 AllArtboards / SelectedArtboards 模式生效）</summary>
    [ObservableProperty]
    private ExportPathOption _exportPathOption = ExportPathOption.ImageOnly;

    /// <summary>切片文件前缀输入是否可见</summary>
    public bool IsFileNameVisible => SelectedMode == ExportMode.Slices;
    public bool IsLayerMode => SelectedMode == ExportMode.Layers;
    [ObservableProperty] private bool _layerExportRecursive;
    [ObservableProperty] private bool _layerExportSelectedOnly = true;
    public bool ShowLayerSelection => IsLayerMode && LayerExportRecursive;
    public string LayerModeHint => !LayerExportRecursive ? "最外层导出，图层组整体保存。"
        : LayerExportSelectedOnly ? "导出 PS 中选中的图层或图层组。" : "逐层导出，包含组内图层。";
    partial void OnLayerExportSelectedOnlyChanged(bool value)
    {
        OnPropertyChanged(nameof(LayerModeHint));
        OnPropertyChanged(nameof(CurrentModeHint));
        SaveBehaviorSettings();
    }
    [RelayCommand] private void SetLayerExportSelectedOnly(object? value)
        => LayerExportSelectedOnly = ParseBooleanCommandParameter(value);
    partial void OnLayerExportRecursiveChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLayerSelection));
        OnPropertyChanged(nameof(LayerModeHint));
        OnPropertyChanged(nameof(CurrentModeHint));
        SaveBehaviorSettings();
    }
    [RelayCommand] private void SetLayerExportRecursive(object? value)
        => LayerExportRecursive = ParseBooleanCommandParameter(value);

    /// <summary>快速导出图片名称、图层导出文件前缀是否可见</summary>
    public bool IsImageNameVisible => SelectedMode is ExportMode.QuickExport or ExportMode.Layers;

    public string ImageNameLabel => SelectedMode == ExportMode.QuickExport ? "图片名称" : "文件前缀";

    public string ImageNamePlaceholder => SelectedMode == ExportMode.QuickExport
        ? "留空使用 Photoshop 默认名称"
        : "留空使用文档名或图层名";

    /// <summary>是否为画板模式（控制导出选项下拉框显示）</summary>
    public bool IsArtboardMode => SelectedMode is ExportMode.AllArtboards or ExportMode.SelectedArtboards;

    public bool CanCancel =>
        IsExporting &&
        !IsCancelling;

    public bool CanChangeMode => !IsBusy;

    public bool CanUseArtboards => HasArtboards && CanChangeMode;

    public bool CanUseSlices => HasSlices && CanChangeMode;

    public bool HasExportTarget => !string.IsNullOrWhiteSpace(ResolvedExportPath);

    public string ApplicationTitle
    {
        get
        {
            var version = typeof(MainViewModel).Assembly.GetName().Version;
            return version is null ? "PSexp" : $"PSexp v{version.Major}.{version.Minor}.{version.Build}";
        }
    }
    public string CurrentModeTitle => SelectedMode switch
    {
        ExportMode.QuickExport => "快速导出",
        ExportMode.AllArtboards => "画板导出",
        ExportMode.SelectedArtboards => "画板导出",
        ExportMode.Layers => "图层导出",
        ExportMode.Slices => "切片导出",
        _ => "导出"
    };

    public string CurrentModeHint => SelectedMode switch
    {
        ExportMode.QuickExport => "导出当前 Photoshop 文档",
        ExportMode.AllArtboards => HasArtboards ? $"导出全部 {ArtboardCount} 个画板" : "当前文档没有画板",
        ExportMode.SelectedArtboards => HasArtboards ? "导出 Photoshop 当前选中的画板" : "当前文档没有画板",
        ExportMode.Layers => LayerModeHint,
        ExportMode.Slices => HasSlices ? "按 Photoshop 原生用户切片范围导出图片" : "当前文档没有可用切片",
        _ => string.Empty
    };

    public string DocumentDisplayName => IsPsRunning && !string.IsNullOrEmpty(DocumentProbeError)
        ? !string.IsNullOrEmpty(_documentProbeActionHint) ? "Photoshop 连接权限不一致"
        : HasDocument ? $"{DocumentName}（同步中断）" : "无法读取 Photoshop 文档"
        : IsPsRunning && IsRefreshingStatus && !HasDocument ? "正在识别 Photoshop 文档…"
        : FormatDocumentDisplayName(IsPsRunning, HasDocument, DocumentName);

    public string PsVersionDisplay => FormatPsVersionDisplay(IsPsRunning, PsVersion);

    public string DocumentSizeDisplay => !string.IsNullOrEmpty(DocumentProbeError)
        ? HasDocument ? $"{DocumentSize} · 上次识别" : "文档状态读取失败"
        : FormatDocumentSizeDisplay(HasDocument, DocumentSize);

    public static string FormatDocumentDisplayName(bool isPsRunning, bool hasDocument, string documentName)
        => !isPsRunning
            ? "Photoshop 未启动"
            : hasDocument && !string.IsNullOrWhiteSpace(documentName)
                ? documentName
                : "未打开 Photoshop 文档";

    public static string FormatPsVersionDisplay(bool isPsRunning, string psVersion)
        => isPsRunning && !string.IsNullOrWhiteSpace(psVersion) ? psVersion : "未连接";

    public static string FormatDocumentSizeDisplay(bool hasDocument, string documentSize)
        => hasDocument && !string.IsNullOrWhiteSpace(documentSize) ? documentSize : "无尺寸数据";

    public string ReadinessText
    {
        get
        {
            if (IsExporting) return IsCancelling ? "正在停止当前任务..." : ExportStatusText;
            if (!IsPsRunning) return "请先打开 Photoshop";
            if (!string.IsNullOrEmpty(_documentProbeActionHint)) return _documentProbeActionHint;
            if (!string.IsNullOrEmpty(DocumentProbeError)) return HasDocument
                ? "文档同步失败，可重试刷新；导出前会重新识别" : "无法读取文档，请刷新并查看日志";
            if (!IsBridgeAvailable) return "Photoshop 连接不可用";
            if (!HasDocument) return "请在 Photoshop 中打开一个文档";
            if (!HasExportTarget) return DestinationHint;
            if (SelectedMode is ExportMode.AllArtboards or ExportMode.SelectedArtboards && !HasArtboards)
                return "当前文档没有画板";
            if (SelectedMode == ExportMode.Slices && !HasSlices)
                return "当前文档没有可用切片";
            return "准备就绪";
        }
    }

    // ---------- 设置页：额外选项 ----------
    [ObservableProperty]
    private bool _layerExportWithDocName = true;

    [ObservableProperty]
    private bool _layerExportShowHint;

    [ObservableProperty]
    private bool _autoUseDocNameAsPrefix;

    [ObservableProperty]
    private AppThemeMode _themeMode = AppThemeMode.Light;

    [ObservableProperty] private bool _enableBlankDrag = true;
    [ObservableProperty] private bool _enableQualityHoverAnimation = true;
    [ObservableProperty] private int _dockRetractDelayMs = 420;

    partial void OnEnableBlankDragChanged(bool value) => SaveBehaviorSettings();
    partial void OnEnableQualityHoverAnimationChanged(bool value) => SaveBehaviorSettings();
    partial void OnDockRetractDelayMsChanged(int value) => SaveBehaviorSettings();

    [RelayCommand] private void SetEnableBlankDrag(object? value) => EnableBlankDrag = ParseBooleanCommandParameter(value);
    [RelayCommand] private void SetEnableQualityHoverAnimation(object? value) => EnableQualityHoverAnimation = ParseBooleanCommandParameter(value);
    [RelayCommand] private void SetDockRetractDelay(object? value)
    {
        if (int.TryParse(value?.ToString(), out var ms) && ms is 250 or 420 or 800) DockRetractDelayMs = ms;
    }

    [ObservableProperty] private string _settingsStatusText = "偏好更改后自动保存";
    public string DefaultExportSummary => (_settings.Current.DefaultFormat == ExportFormat.Png
        ? "PNG 无损" : $"JPG · 质量 {_settings.Current.DefaultQuality}%") + "\n" + DescribeDefaultDestination(_settings.Current);

    public string DataDirectory => _settings.DataDirectory;

    // ---------- 视图导航 ----------
    /// <summary>当前选中的导出模式（左侧导航栏）</summary>
    [ObservableProperty]
    private ExportMode _selectedMode = ExportMode.QuickExport;

    /// <summary>当前是否显示设置视图（true=设置页，false=导出页）</summary>
    [ObservableProperty]
    private bool _isViewSetting;

    public ExportFormat[] AvailableFormats { get; }

    /// <summary>所有导出模式（左侧导航栏绑定用）</summary>
    public ExportMode[] AvailableModes { get; }

    /// <summary>画板导出路径选项列表</summary>
    public ExportPathOption[] AvailableExportOptions { get; } = Enum.GetValues<ExportPathOption>();

    /// <summary>格式变化时同步 IsJpgFormat（UI 据此控制 JPG 质量行显示/隐藏）</summary>
    partial void OnFormatChanged(ExportFormat value)
    {
        IsJpgFormat = value == ExportFormat.Jpg;
        QueueParameterMemory();
    }

    /// <summary>导出模式切换时刷新派生属性通知</summary>
    partial void OnSelectedModeChanged(ExportMode value)
    {
        SwitchPresetWorkspace(value);
        if (value == ExportMode.QuickExport) ImageName = _quickImageName;
        else if (value == ExportMode.Layers) ImageName = _layerImagePrefix;
        QueueParameterMemory();
        OnPropertyChanged(nameof(IsFileNameVisible));
        OnPropertyChanged(nameof(IsLayerMode));
        OnPropertyChanged(nameof(ShowLayerSelection));
        OnPropertyChanged(nameof(IsImageNameVisible));
        OnPropertyChanged(nameof(ImageNameLabel));
        OnPropertyChanged(nameof(ImageNamePlaceholder));
        OnPropertyChanged(nameof(IsArtboardMode));
        OnPropertyChanged(nameof(CurrentModeTitle));
        OnPropertyChanged(nameof(CurrentModeHint));
        OnPropertyChanged(nameof(ReadinessText));
        RefreshCanExecute();
    }

    public ObservableCollection<ArtboardSelectionViewModel> Artboards { get; }

    public ObservableCollection<LogLineViewModel> Logs { get; }

    /// <summary>实时监控到的导出文件列表（导出过程中累积）</summary>
    public ObservableCollection<ExportedFileViewModel> ExportedFiles { get; }

    // ---------- 派生 ----------
    public bool CanExport =>
        !IsBusy && !IsRefreshingStatus &&
        IsPsRunning &&
        IsBridgeAvailable &&
        HasDocument &&
        HasExportTarget &&
        (SelectedMode is not (ExportMode.AllArtboards or ExportMode.SelectedArtboards) || HasArtboards) &&
        (SelectedMode != ExportMode.Slices || HasSlices);

    // 上一次文档状态（用于检测变化，避免日志刷屏）
    private bool _lastHasDocument = false;
    private string _lastDocName = string.Empty;

    partial void OnIsRefreshingStatusChanged(bool value)
    {
        OnPropertyChanged(nameof(DocumentDisplayName));
        OnPropertyChanged(nameof(CanRefreshStatus));
        RefreshStatusCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        RefreshUpdateCommands();
        OnPropertyChanged(nameof(CanRefreshStatus));
        RefreshStatusCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsPsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(DocumentDisplayName));
        OnPropertyChanged(nameof(PsVersionDisplay));
    }

    partial void OnPsVersionChanged(string value) => OnPropertyChanged(nameof(PsVersionDisplay));

    partial void OnDocumentNameChanged(string value) => OnPropertyChanged(nameof(DocumentDisplayName));

    partial void OnDocumentSizeChanged(string value) => OnPropertyChanged(nameof(DocumentSizeDisplay));

    partial void OnHasDocumentChanged(bool value)
    {
        OnPropertyChanged(nameof(DestinationHint));
        OnPropertyChanged(nameof(DocumentDisplayName));
        OnPropertyChanged(nameof(DocumentSizeDisplay));
    }

    private void ResetDocumentState()
    {
        HasDocument = false;
        DocumentName = string.Empty;
        UpdateDocumentDirectory(string.Empty);
        DocumentSize = string.Empty;
        HasArtboards = false;
        ArtboardCount = 0;
        HasSlices = false;
        Artboards.Clear();
        _lastHasDocument = false;
        _lastDocName = string.Empty;
    }

    [ObservableProperty] private string _documentProbeError = string.Empty;
    private DateTime _lastProbeErrorLoggedUtc;
    private string _lastProbeErrorLogged = string.Empty;
    private string _documentProbeActionHint = string.Empty;

    partial void OnDocumentProbeErrorChanged(string value)
    {
        OnPropertyChanged(nameof(DocumentDisplayName));
        OnPropertyChanged(nameof(DocumentSizeDisplay));
        OnPropertyChanged(nameof(ReadinessText));
    }

    private void RecordDocumentProbeFailure(Exception? error, bool preserveLastDocument = false)
    {
        // A failed read cannot prove that the last successfully read document was closed.
        if (!preserveLastDocument) ResetDocumentState();
        _documentProbeActionHint = (error as PsNotConnectedException)?.UserHint ?? string.Empty;
        DocumentProbeError = error?.Message ?? "未知识别错误";
        ExportStatusText = "文档识别失败，请刷新；详情见日志";
        if (_lastProbeErrorLogged != DocumentProbeError || (DateTime.UtcNow - _lastProbeErrorLoggedUtc).TotalSeconds >= 30)
        {
            _lastProbeErrorLogged = DocumentProbeError;
            _lastProbeErrorLoggedUtc = DateTime.UtcNow;
            AppendLog("Error", "文档探测失败：" + DocumentProbeError);
            AppendLog("Debug", "文档探测完整异常：" + error);
        }
        RefreshCanExecute();
    }

    // ---------- 状态机：自动探测 ----------
    // Refresh is always internal COM; only explicit export preflight may opt into external UXP fallback.
    private async Task RefreshStatusAsync(bool silent, bool quiet = true)
    {
        if (!CanRefreshStatus) return;
        IsRefreshingStatus = true;
        _lastDocumentEventAttemptVersion = _documentEvents?.Version ?? 0;
        try
        {
            var info = _ps.DetectProcess();
            if (_documentProcessId != info.ProcessId)
            {
                ResetDocumentState();
                _documentProbeActionHint = string.Empty;
                DocumentProbeError = string.Empty;
                _documentProcessId = info.ProcessId;
            }
            IsPsRunning = info.IsRunning;
            PsVersion = info.Version;
            PsExecutablePath = info.ExecutablePath;
            IsBridgeAvailable = _ps.IsBridgeAvailable;

            if (!IsPsRunning || !IsBridgeAvailable)
            {
                _documentProbeActionHint = string.Empty;
                DocumentProbeError = string.Empty;
                ResetDocumentState();
                RefreshCanExecute();
                return;
            }

            var (doc, probeResult) = quiet
                ? await _ps.ProbeDocumentInfoQuietlyAsync()
                : await _ps.ProbeDocumentInfoAsync();
            // 已发出的自动 COM 读取无需强制中断，但结果不能在神灯动画中途改动界面。
            if (quiet) await WaitForWindowAnimationIdleAsync();
            // 把脚本端日志透传到 UI 日志面板
            foreach (var log in probeResult.Logs)
                AppendLog(log.Level.ToString(), log.Message);
            if (!probeResult.Success)
            {
                RecordDocumentProbeFailure(probeResult.Error, preserveLastDocument: quiet && HasDocument);
                return;
            }
            if (!string.IsNullOrEmpty(DocumentProbeError) && ExportStatusText == "文档识别失败，请刷新；详情见日志")
                ExportStatusText = doc.IsOpen ? "文档识别已恢复，可以导出" : "当前没有打开文档";
            _documentProbeActionHint = string.Empty;
            DocumentProbeError = string.Empty;
            _lastProbeErrorLogged = string.Empty;
            if (!doc.IsOpen)
            {
                ResetDocumentState();
                RefreshCanExecute();
                return;
            }

            HasDocument = true;
            DocumentName = doc.Name;
            UpdateDocumentDirectory(doc.Path);
            DocumentSize = doc.Width > 0 ? $"{doc.Width} × {doc.Height}" : string.Empty;
            HasArtboards = doc.HasArtboards;
            ArtboardCount = doc.ArtboardCount;
            HasSlices = doc.HasSlices;

            // 诊断：仅在文档状态变化时记录，避免重复刷新刷屏
            if (doc.IsOpen != _lastHasDocument || doc.Name != _lastDocName)
            {
                AppendLog("Debug", $"文档状态变化: HasDocument={HasDocument} DocName='{doc.Name}' Artboards={doc.ArtboardCount} Slices={doc.HasSlices}");
                _lastHasDocument = doc.IsOpen;
                _lastDocName = doc.Name;
            }

            // 画板数量从 document_info 拿（用于按钮启用 + 顶部徽标），不再拉取画板列表
            // UI 不展示画板列表，画板选择状态由 PS 内部决定（点击「选中导出」导出 PS 当前选择）
            if (!HasArtboards)
            {
                Artboards.Clear();
            }
        }
        catch (Exception ex)
        {
            RecordDocumentProbeFailure(ex, preserveLastDocument: quiet && HasDocument);
        }
        finally
        {
            IsRefreshingStatus = false;
            RefreshCanExecute();
        }
    }

    public static bool CanRefreshOnActivation(
        bool enabled,
        bool isBusy,
        bool isRefreshing,
        bool suppressNextActivation)
        => enabled && !isBusy && !isRefreshing && !suppressNextActivation;

    public async Task RefreshOnActivationAsync(Func<bool>? isForeground = null, bool onlyIfChanged = false)
    {
        if (onlyIfChanged && !HasPendingDocumentChanges) return;
        var suppress = !onlyIfChanged && _suppressNextActivationRefresh;
        if (!onlyIfChanged) _suppressNextActivationRefresh = false;
        if (!CanRefreshOnActivation(AutoRefreshOnActivated, IsBusy, IsRefreshingStatus, suppress)
            || isForeground?.Invoke() == false) return;

        AppendLog("Debug", onlyIfChanged
            ? "[document-events] 前台消费变化标记，执行一次 COM 文档识别（不派发外部脚本）"
            : "PSexp 主窗口获得前台焦点，执行一次 COM 文档识别（不派发外部脚本）");
        await RefreshStatusAsync(silent: true, quiet: true);
        await EnsureDocumentEventsAsync(isForeground);
    }

    private void SuppressNextActivationRefresh() => _suppressNextActivationRefresh = true;

    private void RefreshCanExecute()
    {
        StartExportCommand.NotifyCanExecuteChanged();
        CancelExportCommand.NotifyCanExecuteChanged();
        RefreshStatusCommand.NotifyCanExecuteChanged();
        LaunchPsCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanChangeMode));
        OnPropertyChanged(nameof(CanUseArtboards));
        OnPropertyChanged(nameof(CanUseSlices));
        OnPropertyChanged(nameof(HasExportTarget));
        OnPropertyChanged(nameof(CurrentModeHint));
        OnPropertyChanged(nameof(ReadinessText));
    }

    private bool CanRunRefreshStatus() => CanRefreshStatus;

    // ---------- 命令：手动刷新 ----------
    [RelayCommand(CanExecute = nameof(CanRunRefreshStatus))]
    private async Task RefreshStatusAsync() => await RefreshStatusAsync(silent: false);

    // ---------- 命令：启动 PS ----------
    [RelayCommand(CanExecute = nameof(CanLaunchPs))]
    private async Task LaunchPsAsync()
    {
        IsBusy = true;
        AppendLog("Info", "正在启动 Photoshop...");
        var ok = await _ps.TryLaunchAsync(_settings.Current.PhotoshopExePath);
        AppendLog(ok ? "Info" : "Error", ok ? "Photoshop 启动中" : "无法启动 Photoshop，请手动打开");
        IsBusy = false;
        await RefreshStatusAsync(silent: false);
    }
    private bool CanLaunchPs() => !IsPsRunning;

    // ---------- 命令：选择导出目录 ----------
    [RelayCommand]
    private async Task BrowseExportFolderAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        var provider = desktop.MainWindow?.StorageProvider;
        if (provider is null) return;
        var result = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false, Title = "选择导出文件夹" });
        if (result.Count > 0)
        {
            Destination = ExportDestination.Custom;
            ExportPath = result[0].Path.LocalPath;
        }
    }

    // ---------- 命令：保存为默认 ----------
    [RelayCommand]
    private void SaveAsDefault()
    {
        if (_settings.Save(CreateAppSettings()))
        {
            ExportStatusText = "默认参数已保存";
            SettingsStatusText = "默认导出参数已保存";
            OnPropertyChanged(nameof(DefaultExportSummary));
            AppendLog("Info", "默认导出参数已保存");
        }
        else
        {
            ExportStatusText = "设置保存失败";
            SettingsStatusText = "设置保存失败，请检查数据目录写入权限";
            AppendLog("Error", "设置保存失败，请检查应用数据目录的写入权限");
        }
    }

    private AppSettings CreateAppSettings()
    {
        var current = _settings.Current;
        return new AppSettings
        {
            DefaultExportPath = ExportPath,
            DefaultDestination = Destination,
            DefaultFormat = Format,
            DefaultQuality = Math.Clamp(Quality, 1, 100),
            DefaultAutoRename = AutoRename,
            SliceFilePrefix = FileName,
            SliceFolderName = SliceFolderName,
            QuickImageName = _quickImageName,
            LayerImagePrefix = _layerImagePrefix,
            LastExportMode = SelectedMode,
            LastArtboardPathOption = ExportPathOption,
            LastSettingsSection = SelectedSettingsSection,
            ShowSettingsOnStartup = IsViewSetting,
            PhotoshopExePath = current.PhotoshopExePath,
            LayerExportWithDocName = LayerExportWithDocName,
            LayerExportRecursive = LayerExportRecursive,
            LayerExportSelectedOnly = LayerExportSelectedOnly,
            LayerExportShowHint = LayerExportShowHint,
            AutoUseDocNameAsPrefix = AutoUseDocNameAsPrefix,
            ThemeMode = ThemeMode,
            StartAlwaysOnTop = IsAlwaysOnTop,
            AutoRefreshOnActivated = AutoRefreshOnActivated,
            EnableEdgeSnap = EnableEdgeSnap,
            AutoCheckForUpdates = AutoCheckForUpdates,
            AutoDownloadUpdates = AutoDownloadUpdates,
            CloseToTray = CloseToTray,
            EnableBlankDrag = EnableBlankDrag,
            EnableQualityHoverAnimation = EnableQualityHoverAnimation,
            DockRetractDelayMs = DockRetractDelayMs,
            WindowPlacement = _windowPlacement,
            WindowWidth = _windowWidth,
            WindowHeight = _windowHeight
        };
    }

    public (double Width, double Height) GetSavedWindowSize() => (_windowWidth, _windowHeight);

    public bool SaveWindowSize(double width, double height)
    {
        var (normalizedWidth, normalizedHeight) = SettingsService.NormalizeWindowSize(width, height);
        if (Math.Abs(_windowWidth - normalizedWidth) < 0.5
            && Math.Abs(_windowHeight - normalizedHeight) < 0.5)
            return true;

        _windowWidth = normalizedWidth;
        _windowHeight = normalizedHeight;
        if (!_settings.Save(CreateAppSettings()))
        {
            ExportStatusText = "窗口尺寸保存失败";
            AppendLog("Error", "窗口尺寸保存失败，请检查数据目录写入权限");
            return false;
        }
        return true;
    }

    private bool SaveBehaviorSettings()
    {
        if (!_settingsInitialized || _applyingPreset) return true;
        var settingsSaved = _settings.Save(CreateAppSettings());
        var presetsSaved = SavePresetState();
        if (!settingsSaved || !presetsSaved)
        {
            ExportStatusText = "设置自动保存失败";
            SettingsStatusText = "自动保存失败，请检查数据目录写入权限";
            AppendLog("Error", "设置自动保存失败，请检查数据目录写入权限");
        }
        else SettingsStatusText = "偏好设置已自动保存";
        return settingsSaved && presetsSaved;
    }

    // ---------- 命令：左侧导航 - 选择导出模式 ----------
    [RelayCommand]
    private void SelectMode(ExportMode? mode)
    {
        if (mode is null) return;
        SelectedMode = mode.Value;
        IsViewSetting = false;
    }

    // ---------- 命令：左侧导航 - 切换到设置视图 ----------
    [RelayCommand]
    private void ShowSettings()
    {
        if (IsUpdateAvailable) SelectedSettingsSection = 3;
        IsViewSetting = true;
    }

    // ---------- 命令：开始执行（根据当前选中的模式触发导出） ----------
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task StartExportAsync()
    {
        if (IsViewSetting) return;
        await RefreshStatusAsync(silent: true, quiet: false);
        if (!HasDocument || !string.IsNullOrEmpty(DocumentProbeError)) return;

        var exportSettings = Snapshot();
        AppendLog("Info", $"导出请求 mode={SelectedMode} destination={Destination} parameters={System.Text.Json.JsonSerializer.Serialize(exportSettings)}");
        if (SelectedMode == ExportMode.QuickExport)
            exportSettings.ExpectedFileCount = 1;
        if (SelectedMode == ExportMode.AllArtboards)
            exportSettings.ExpectedFileCount = ArtboardCount;
        if (!ExportSettingsValidator.TryPrepare(exportSettings, out var validationError))
        {
            ExportStatusText = validationError;
            AppendLog("Error", validationError);
            return;
        }

        // Normalizing a preset must never replace the remembered custom directory.
        if (IsCustomDestination) ExportPath = exportSettings.ExportPath;
        _exportCts?.Dispose();
        _exportCts = new CancellationTokenSource();
        switch (SelectedMode)
        {
            case ExportMode.QuickExport:
                await RunExportAsync(ct => _export.QuickExportAsync(exportSettings, OnExportProgress, ct), "快速导出", _exportCts.Token);
                break;
            case ExportMode.AllArtboards:
                if (!HasArtboards) { AppendLog("Warn", "当前文档没有画板"); return; }
                await RunExportAsync(ct => _export.ExportAllArtboardsAsync(exportSettings, OnExportProgress, ct), "全部画板导出", _exportCts.Token);
                break;
            case ExportMode.SelectedArtboards:
                await RunExportAsync(ct => _export.ExportSelectedArtboardsAsync(exportSettings, OnExportProgress, ct), "选中导出", _exportCts.Token);
                break;
            case ExportMode.Layers:
                await RunExportAsync(ct => _export.ExportLayersAsync(exportSettings, OnExportProgress, ct), "图层导出", _exportCts.Token);
                break;
            case ExportMode.Slices:
                await RunExportAsync(ct => _export.ExportSlicesAsync(exportSettings, OnExportProgress, ct), "切片导出", _exportCts.Token);
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelExport()
    {
        if (!CanCancel) return;
        IsCancelling = true;
        ExportStatusText = "正在停止等待...";
        AppendLog("Warn", $"正在停止等待：{ExportTaskName}；Photoshop 可能仍会继续处理");
        _exportCts?.Cancel();
        RefreshCanExecute();
    }

    /// <summary>导出进度回调（从后台线程触发，需切回 UI 线程）</summary>
    private void OnExportProgress(string newFileName, int exportedCount)
    {
        void UpdateProgress()
        {
            ExportedCount = exportedCount;
            LastExportedFile = newFileName;
            ExportStatusText = FormatExportProgressText(exportedCount, _activeExpectedFileCount);
            ExportedFiles.Insert(0, new ExportedFileViewModel
            {
                Time = DateTime.Now.ToString("HH:mm:ss"),
                FileName = newFileName
            });
            // 限制列表长度，避免内存膨胀
            while (ExportedFiles.Count > 200)
                ExportedFiles.RemoveAt(ExportedFiles.Count - 1);
        }

        if (Dispatcher.UIThread.CheckAccess())
            UpdateProgress();
        else
            Dispatcher.UIThread.Post(UpdateProgress);
    }

    public static string FormatExportProgressText(int exportedCount, int? expectedFileCount)
        => expectedFileCount is > 0
            ? $"正在导出... 已生成 {exportedCount} / {expectedFileCount.Value} 个文件"
            : $"正在导出... 已生成 {exportedCount} 个文件";

    private async Task RunExportAsync(
        Func<CancellationToken, Task<PsBridgeResult>> factory,
        string title,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ResolvedExportPath))
        {
            AppendLog("Error", "请先选择导出路径");
            return;
        }

        IsBusy = true;
        IsExporting = true;
        IsCancelling = false;
        ExportTaskName = title;
        ExportedCount = 0;
        LastExportedFile = string.Empty;
        CanOpenExportFolder = false;
        _lastSuccessfulExportDirectory = string.Empty;
        ExportedFiles.Clear();
        _activeExpectedFileCount = SelectedMode == ExportMode.AllArtboards
            ? ArtboardCount
            : null;
        ExportStatusText = _activeExpectedFileCount is > 0
            ? $"{title} 开始... 0 / {_activeExpectedFileCount.Value}"
            : $"{title} 开始...";
        RefreshCanExecute();
        AppendLog("Info", $"▶ {title} 开始");

        try
        {
            var result = await factory(cancellationToken);
            foreach (var log in result.Logs)
                AppendLog(log.Level.ToString(), log.Message);
            ExportedCount = Math.Max(ExportedCount, result.ExportedCount);
            if (result.Success)
            {
                _lastSuccessfulExportDirectory = result.OutputFiles
                    .Select(Path.GetDirectoryName)
                    .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                    ?? ResolvedExportPath;
                CanOpenExportFolder = Directory.Exists(_lastSuccessfulExportDirectory);
                ExportStatusText = $"{title} 完成 · 共 {ExportedCount} 个文件";
                AppendLog("Info", $"✔ {title} 完成");
            }
            else
            {
                ExportStatusText = $"{title} 失败：{result.Error?.Message ?? "未知错误"}";
                AppendLog("Error", $"✘ {title} 失败: {result.Error?.Message ?? "未知错误"}");
                AppendLog("Debug", $"{title} 完整错误：{result.Error}；输出文件={System.Text.Json.JsonSerializer.Serialize(result.OutputFiles)}");
            }
        }
        catch (OperationCanceledException)
        {
            ExportStatusText = $"{title} 已停止等待";
            AppendLog("Warn", $"■ {title} 已停止等待；Photoshop 可能仍在执行");
        }
        catch (Exception ex)
        {
            ExportStatusText = $"{title} 异常：{ex.Message}";
            AppendLog("Error", $"✘ {title} 异常: {ex.Message}");
            AppendLog("Debug", $"{title} 完整异常：{ex}");
        }
        finally
        {
            IsBusy = false;
            IsExporting = false;
            IsCancelling = false;
            _exportCts?.Dispose();
            _exportCts = null;
            _activeExpectedFileCount = null;
            RefreshCanExecute();
            // 导出结束后把窗口拉回前台（PS 执行 .psjs 时会抢焦点）。
            // 仅当窗口确实位于后台时，才抑制这一次程序内部激活。
            ActivateMainWindow();
        }
    }

    /// <summary>把 PsExporter 主窗口激活到前台，并抑制这一次程序内部激活刷新。</summary>
    private void ActivateMainWindow()
    {
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow is { } window
                && !window.IsActive)
            {
                SuppressNextActivationRefresh();
                window.Activate();
            }
        }
        catch { /* ignore */ }
    }

    private ExportSettings Snapshot()
    {
        var effectiveImageName = ResolveLayerImageName(
            SelectedMode,
            ImageName,
            DocumentName,
            AutoUseDocNameAsPrefix);

        return new ExportSettings
        {
            ExportPath = ResolvedExportPath,
            DocumentName = DocumentName,
            Format = Format,
            Quality = Quality,
            AutoRename = AutoRename,
            FileName = SelectedMode == ExportMode.Slices ? FileName : string.Empty,
            SliceFolderName = SelectedMode == ExportMode.Slices ? SliceFolderName : string.Empty,
            ImageName = effectiveImageName,
            LayerExportWithDocName = LayerExportWithDocName,
            LayerExportRecursive = LayerExportRecursive,
            LayerExportSelectedOnly = LayerExportSelectedOnly,
            LayerExportShowHint = LayerExportShowHint,
            ExportPathOption = ExportPathOption
        };
    }

    public static string ResolveLayerImageName(
        ExportMode mode,
        string imageName,
        string documentName,
        bool autoUseDocumentName)
    {
        if (mode == ExportMode.QuickExport) return imageName;
        if (mode != ExportMode.Layers) return string.Empty;
        return autoUseDocumentName
            ? Path.GetFileNameWithoutExtension(documentName)
            : imageName;
    }

    public static bool ParseBooleanCommandParameter(object? value)
    {
        if (value is bool boolean) return boolean;
        return bool.TryParse(value?.ToString(), out var parsed) && parsed;
    }

    partial void OnExportPathChanged(string value)
    {
        NotifyExportDestinationChanged();
        QueueParameterMemory();
    }

    partial void OnFileNameChanged(string value)
    {
        RefreshCanExecute();
        QueueParameterMemory();
    }

    partial void OnIsExportingChanged(bool value)
    {
        RefreshUpdateCommands();
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(ReadinessText));
        OnPropertyChanged(nameof(EffectiveTopmost));
    }

    partial void OnIsAlwaysOnTopChanged(bool value)
    {
        OnPropertyChanged(nameof(EffectiveTopmost));
        SaveBehaviorSettings();
    }

    partial void OnIsEdgeDockedChanged(bool value) => OnPropertyChanged(nameof(EffectiveTopmost));

    partial void OnAutoRefreshOnActivatedChanged(bool value)
    {
        SaveBehaviorSettings();
        _documentEvents?.SetEnabled(value);
    }

    partial void OnEnableEdgeSnapChanged(bool value) => SaveBehaviorSettings();

    partial void OnThemeModeChanged(AppThemeMode value)
    {
        App.ApplyTheme(value);
        SaveBehaviorSettings();
    }

    partial void OnAutoRenameChanged(bool value) => SaveBehaviorSettings();

    partial void OnLayerExportWithDocNameChanged(bool value) => SaveBehaviorSettings();

    partial void OnLayerExportShowHintChanged(bool value) => SaveBehaviorSettings();

    partial void OnAutoUseDocNameAsPrefixChanged(bool value) => SaveBehaviorSettings();

    partial void OnIsCancellingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(ReadinessText));
    }

    // ---------- 日志 ----------
    public void AppendLog(string level, string message)
    {
        // 同时写文件日志（供开发者直接读取）
        _fileLogger.Log(level, message);
        Dispatcher.UIThread.Post(() =>
        {
            Logs.Add(new LogLineViewModel
            {
                Time = DateTime.Now.ToString("HH:mm:ss"),
                Level = level,
                Message = message
            });
            // 限制日志长度
            while (Logs.Count > 500)
                Logs.RemoveAt(0);
        });
    }

    [RelayCommand]
    private void SetTheme(AppThemeMode mode) => ThemeMode = mode;

    [RelayCommand]
    private void SetAutoRename(object? value) => AutoRename = ParseBooleanCommandParameter(value);

    [RelayCommand]
    private void SetLayerExportWithDocName(object? value) => LayerExportWithDocName = ParseBooleanCommandParameter(value);

    [RelayCommand]
    private void SetLayerExportShowHint(object? value) => LayerExportShowHint = ParseBooleanCommandParameter(value);

    [RelayCommand]
    private void SetAutoUseDocNameAsPrefix(object? value) => AutoUseDocNameAsPrefix = ParseBooleanCommandParameter(value);

    [RelayCommand]
    private void SetAlwaysOnTop(object? value) => IsAlwaysOnTop = ParseBooleanCommandParameter(value);

    [RelayCommand]
    private void SetAutoRefreshOnActivated(object? value) => AutoRefreshOnActivated = ParseBooleanCommandParameter(value);

    [RelayCommand]
    private void SetEnableEdgeSnap(object? value) => EnableEdgeSnap = ParseBooleanCommandParameter(value);

    [RelayCommand]
    private void OpenDataDirectory()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{DataDirectory}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ExportStatusText = "无法打开数据目录";
            AppendLog("Error", $"打开数据目录失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ToggleAlwaysOnTop() => IsAlwaysOnTop = !IsAlwaysOnTop;

    [RelayCommand]
    private void OpenExportFolder()
    {
        try
        {
            var path = Path.GetFullPath(
                string.IsNullOrWhiteSpace(_lastSuccessfulExportDirectory)
                    ? ResolvedExportPath
                    : _lastSuccessfulExportDirectory);
            if (!Directory.Exists(path))
            {
                CanOpenExportFolder = false;
                ExportStatusText = "导出目录不存在";
                AppendLog("Warn", $"导出目录不存在: {path}");
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ExportStatusText = "无法打开导出目录";
            AppendLog("Error", $"打开导出目录失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private void SetQuality(object? parameter)
    {
        if (parameter is int val)
        {
            Quality = Math.Clamp(val, 1, 100);
        }
        else if (parameter != null && int.TryParse(parameter.ToString(), out int parsed))
        {
            Quality = Math.Clamp(parsed, 1, 100);
        }
    }

    [RelayCommand]
    private void SetFormat(ExportFormat format)
    {
        Format = format;
    }

    [RelayCommand]
    private void SetExportPathOption(ExportPathOption option)
    {
        ExportPathOption = option;
    }

}
