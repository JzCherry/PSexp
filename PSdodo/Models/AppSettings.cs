namespace PsExporter.Models;

/// <summary>全局应用设置（持久化到 %AppData%/PsExporter/settings.json）</summary>
public sealed class AppSettings
{
    public const double DefaultWindowWidth = 440;
    public const double DefaultWindowHeight = 660;
    public const double MinimumWindowWidth = 360;
    public const double MinimumWindowHeight = 600;
    public const double WindowAspectRatio = 3d / 4d;

    public const double MinimumCommandWindowHeight = 480;
    public const int DefaultExportNoticeDurationSeconds = 5;

    /// <summary>自定义目录；切换到文档位置或桌面时仍保留。</summary>
    public string DefaultExportPath { get; set; } = string.Empty;

    /// <summary>旧配置缺少此项时，保留原先的自定义目录行为。</summary>
    public ExportDestination? DefaultDestination { get; set; }

    public ExportFormat DefaultFormat { get; set; } = ExportFormat.Jpg;

    public int DefaultQuality { get; set; } = 80;

    public bool DefaultAutoRename { get; set; } = true;
    public bool SaveAndCloseAfterExport { get; set; }

    /// <summary>切片文件名前缀，可为空；清空后仍持久保存为空。</summary>
    public string SliceFilePrefix { get; set; } = string.Empty;

    public string SliceFolderName { get; set; } = string.Empty;
    public string QuickImageName { get; set; } = string.Empty;
    public string LayerImagePrefix { get; set; } = string.Empty;
    public string? LayerFilePrefix { get; set; }
    public string ArtboardFilePrefix { get; set; } = string.Empty;
    public LayerPrefixMode? LayerPrefixMode { get; set; }
    public ExportMode LastExportMode { get; set; } = ExportMode.QuickExport;
    public ExportPathOption LastArtboardPathOption { get; set; } = ExportPathOption.ImageOnly;
    public bool ShowSettingsOnStartup { get; set; }


    public string? PhotoshopExePath { get; set; }

    /// <summary>导出一级图层时文件名带文档名前缀</summary>
    public bool LayerExportWithDocName { get; set; } = true;
    public bool LayerExportRecursive { get; set; }
    public bool LayerExportSelectedOnly { get; set; } = true;
    public bool LayerTopLevelSelectedOnly { get; set; }
    public bool? LayerScopeSelectedOnly { get; set; }

    /// <summary>兼容旧配置保留；当前版本不再使用 Photoshop 提示窗。</summary>
    public bool LayerExportShowHint { get; set; } = false;

    /// <summary>动作启动后自动获取 PS 文档名作为导出命名</summary>
    public bool AutoUseDocNameAsPrefix { get; set; } = false;

    /// <summary>应用主题</summary>
    public AppThemeMode ThemeMode { get; set; } = AppThemeMode.Light;
    public Dictionary<ExportMode, ExportHighlight> ExportHighlights { get; set; } = [];

    /// <summary>启动后保持窗口置顶</summary>
    public bool StartAlwaysOnTop { get; set; }

    /// <summary>PSexp 主窗口获得前台焦点时，自动通过 COM 内部读取一次文档信息</summary>
    public bool AutoRefreshOnActivated { get; set; } = true;

    /// <summary>拖拽窗口贴到屏幕边缘吸附与 Q 弹动效</summary>
    public bool EnableEdgeSnap { get; set; } = true;

    public bool AutoCheckForUpdates { get; set; } = true;
    public bool AutoDownloadUpdates { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool ShowExportNotice { get; set; } = true;
    public bool ExportTempDocumentsToDesktop { get; set; } = true;
    public int ExportNoticeDurationSeconds { get; set; } = DefaultExportNoticeDurationSeconds;
    public ExportNoticeTarget ExportNoticeTarget { get; set; }
    public int DockRetractDelayMs { get; set; } = 420;

    /// <summary>系统级唤起热键的默认组合；空串表示不注册热键。</summary>
    public const string DefaultSummonHotkey = "Ctrl+Alt+E";

    public string SummonHotkey { get; set; } = DefaultSummonHotkey;

    /// <summary>旧版的跟随鼠标开关，供旧配置迁移与回退版本使用。</summary>
    public bool SummonAtPointer { get; set; }

    private PointerSummonMode? _summonPointerMode;
    public PointerSummonMode SummonPointerMode
    {
        get => _summonPointerMode switch
        {
            null => SummonAtPointer ? PointerSummonMode.Automatic : PointerSummonMode.Off,
            PointerSummonMode.Automatic => PointerSummonMode.Automatic,
            PointerSummonMode.Center => PointerSummonMode.Center,
            _ => PointerSummonMode.Off
        };
        set => _summonPointerMode = value;
    }

    public bool SummonReturnAnimation { get; set; }

    public WindowPlacementState? WindowPlacement { get; set; }

    /// <summary>上次正常窗口宽度</summary>
    public double WindowWidth { get; set; } = DefaultWindowWidth;

    /// <summary>上次正常窗口高度</summary>
    public double WindowHeight { get; set; } = DefaultWindowHeight;

    /// <summary>导出指令窗口上次的参数来源；默认仍是「指定预设」，与改名前的打开状态一致。</summary>
    public bool ExportCommandUsePreset { get; set; } = true;

    /// <summary>导出指令窗口里各板块上次选中的预设；键是板块名，值为预设 ID。</summary>
    public Dictionary<string, string?> ExportCommandPresets { get; set; } = [];

    /// <summary>导出指令窗口上次的尺寸；0 表示从未记录，用默认值。</summary>
    public double ExportCommandWindowWidth { get; set; }

    public double ExportCommandWindowHeight { get; set; }
    public int ExportCommandWindowSizeVersion { get; set; }
}
