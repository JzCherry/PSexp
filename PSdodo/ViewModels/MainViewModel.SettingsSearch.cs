using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PsExporter.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] private string _settingsSearchQuery = "";
    private HashSet<string> _matchingSettings = SettingsSearchEntries.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
    private sealed record SettingsSearchEntry(string Key, string Keywords, bool RequiresExportNotice = false);
    private static readonly SettingsSearchEntry[] SettingsSearchEntries =
    [
        new("Theme", "界面 外观 主题 配色 颜色 跟随系统 浅色 深色 黑暗 白色 theme appearance light dark system"),
        new("Highlight", "外观 导出高亮 主题色 强调色 配色 颜色 快速导出 画板导出 图层导出 切片导出 highlight accent color"),
        new("Tray", "窗口 关闭后常驻后台 常驻 托盘 隐藏 后台运行 退出 close tray background"),
        new("Edge", "窗口 贴边收纳 屏幕边缘 悬浮入口 自动隐藏 展开 吸附 dock edge"),
        new("Delay", "窗口 贴边收纳 收回等待 延时 延迟 快速 标准 从容 250 420 800 毫秒 delay"),
        new("Hotkey", "窗口 唤起 热键 快捷键 全局 组合键 前台 弹出 面板 按键  summon hotkey shortcut key"),
        new("HotkeyProgram", "窗口 唤起 热键 快捷键 快捷键绑定程序 绑定程序 Photoshop PS 应用 全局 未绑定 hotkey binding program"),
        new("Pointer", "窗口 唤起 跟随 鼠标 光标 自动 鼠标中心 居中 旁边 出现 位置 移动 pointer mouse cursor follow center"),
        new("ReturnAnimation", "窗口 动画跟随 动画 跟随 鼠标 贴边 失焦 回收 返回 收纳 animation return"),
        new("Startup", "开机 静默 启动 登录 自启 后台 托盘 startup login"),
        new("Sync", "Photoshop PS 联动 自动同步 文档识别 读取 置顶 焦点 刷新 document refresh sync"),
        new("Automation", "外部调用 脚本指令 导出指令 导出命令 当前界面 当前参数 当前预设 命令行 自动化 Quicker Windows 运行 PowerShell automation command"),
        new("TempDestination", "导出位置 Temp兼容 临时文件 临时文档 导出到桌面 文档位置"),
        new("Notice", "导出提示 完成提示 通知 提示条 开启 关闭 toast notification"),
        new("NoticeDuration", "导出提示 停留时间 时长 秒 3 5 10 toast duration", RequiresExportNotice: true),
        new("NoticeTarget", "导出提示 点击打开 输出目录 上一级 文件夹 toast folder", RequiresExportNotice: true),
        new("Data", "数据与日志 设置目录 配置目录 预设数据 偏好 默认参数 文件夹 data settings directory configuration"),
        new("Log", "数据与日志 日志目录 运行记录 导出记录 报错 调试 psx.log log debug directory"),
        new("Check", "自动更新 自动检测 启动检查 检测更新 检查更新 周期 update check"),
        new("Download", "自动更新 后台下载 自动下载 安装 校验 更新包 update download"),
        new("Version", "版本信息 关于 PSexp 当前版本 检查更新 更新内容 发布说明 下载进度 重启安装 软件更新 about version release changelog update")
    ];

    public event Action<bool>? SettingsNavigationRequested;
    public bool HasSettingsSearchText => !string.IsNullOrEmpty(SettingsSearchQuery);
    public bool IsSearchingSettings => !string.IsNullOrWhiteSpace(SettingsSearchQuery);
    public bool HasNoSettingsSearchResults => IsSearchingSettings && _matchingSettings.Count == 0;
    public string SettingsSearchSummary => IsSearchingSettings ? $"找到 {_matchingSettings.Count} 项" : "";

    public bool ShowThemeSetting => _matchingSettings.Contains("Theme");
    public bool ShowHighlightSetting => _matchingSettings.Contains("Highlight");
    public bool ShowThemeHighlightLine => ShowThemeSetting && ShowHighlightSetting;
    public bool ShowTraySetting => _matchingSettings.Contains("Tray");
    public bool ShowEdgeSetting => _matchingSettings.Contains("Edge");
    public bool ShowDelaySetting => _matchingSettings.Contains("Delay");
    public bool ShowHotkeySetting => _matchingSettings.Contains("Hotkey");
    public bool ShowHotkeyProgramSetting => _matchingSettings.Contains("HotkeyProgram");
    public bool ShowPointerSetting => _matchingSettings.Contains("Pointer");
    public bool ShowReturnAnimationSetting => FollowsPointerOnSummon && _matchingSettings.Contains("ReturnAnimation");
    public bool ShowStartupSetting => _matchingSettings.Contains("Startup");
    public bool ShowAutomationCard => _matchingSettings.Contains("Automation");
    public bool ShowTempDestinationCard => _matchingSettings.Contains("TempDestination");
    public bool ShowNoticeSetting => _matchingSettings.Contains("Notice");
    public bool ShowNoticeDurationSetting => _matchingSettings.Contains("NoticeDuration");
    public bool ShowNoticeTargetSetting => _matchingSettings.Contains("NoticeTarget");
    public bool ShowNoticeCard => ShowNoticeSetting || ShowNoticeDurationSetting || ShowNoticeTargetSetting;
    public bool ShowNoticeLine => ShowNoticeSetting && (ShowNoticeDurationSetting || ShowNoticeTargetSetting);
    public bool ShowNoticeDurationLine => ShowNoticeDurationSetting && ShowNoticeTargetSetting;
    public bool ShowSyncSetting => _matchingSettings.Contains("Sync");
    public bool ShowDataSetting => _matchingSettings.Contains("Data");
    public bool ShowLogSetting => _matchingSettings.Contains("Log");
    public bool ShowCheckSetting => _matchingSettings.Contains("Check");
    public bool ShowDownloadSetting => _matchingSettings.Contains("Download");
    public bool ShowVersionCard => _matchingSettings.Contains("Version");
    // Settings groups: the card shows when any row inside it matches; each row keeps its own filter.
    public bool ShowAppearanceCard => ShowThemeSetting || ShowHighlightSetting;
    private bool ShowPointerSettings => ShowPointerSetting || ShowReturnAnimationSetting;
    private bool ShowHotkeySettings => ShowHotkeySetting || ShowHotkeyProgramSetting;
    public bool ShowWindowCard => ShowTraySetting || ShowEdgeSetting || ShowDelaySetting || ShowStartupSetting;
    public bool ShowSummonCard => ShowHotkeySettings || ShowPointerSettings;
    public bool ShowPhotoshopCard => ShowSyncSetting;
    public bool ShowUpdateCard => ShowCheckSetting || ShowDownloadSetting || ShowVersionCard;
    public bool ShowDataCard => ShowDataSetting || ShowLogSetting;
    // A hairline only exists between two rows that are both on screen.
    public bool ShowTrayEdgeLine => ShowTraySetting && (ShowEdgeSetting || ShowDelaySetting || ShowStartupSetting);
    public bool ShowEdgeDelayLine => ShowEdgeSetting && (ShowDelaySetting || ShowStartupSetting);
    public bool ShowDelayStartupLine => ShowDelaySetting && ShowStartupSetting;
    public bool ShowHotkeyProgramLine => ShowHotkeySetting && ShowHotkeyProgramSetting;
    public bool ShowHotkeyProgramPointerLine => ShowHotkeyProgramSetting && ShowPointerSettings;
    public bool ShowPointerAnimationLine => ShowPointerSetting && ShowReturnAnimationSetting;
    public bool ShowCheckDownloadLine => ShowCheckSetting && (ShowDownloadSetting || ShowVersionCard);
    public bool ShowDownloadVersionLine => ShowDownloadSetting && ShowVersionCard;
    public bool ShowDataLogLine => ShowDataSetting && ShowLogSetting;

    partial void OnSettingsSearchQueryChanged(string value) => RefreshSettingsSearch();

    private void RefreshSettingsSearch()
    {
        var words = (SettingsSearchQuery ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _matchingSettings = SettingsSearchEntries.Where(item => (ShowExportNotice || !item.RequiresExportNotice)
                && (item.Key != "ReturnAnimation" || FollowsPointerOnSummon) && words.All(word =>
                (item.Key == "Version" ? item.Keywords + " " + UpdateVersionLabel : item.Keywords)
                .Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var name in new[]
        {
            nameof(HasSettingsSearchText), nameof(IsSearchingSettings), nameof(HasNoSettingsSearchResults), nameof(SettingsSearchSummary),
            nameof(ShowThemeSetting), nameof(ShowHighlightSetting), nameof(ShowThemeHighlightLine),
            nameof(ShowTraySetting), nameof(ShowEdgeSetting), nameof(ShowDelaySetting), nameof(ShowHotkeySetting), nameof(ShowHotkeyProgramSetting),
            nameof(ShowPointerSetting), nameof(ShowReturnAnimationSetting), nameof(ShowStartupSetting),
            nameof(ShowSyncSetting), nameof(ShowAutomationCard), nameof(ShowTempDestinationCard),
            nameof(ShowNoticeSetting), nameof(ShowNoticeDurationSetting), nameof(ShowNoticeTargetSetting),
            nameof(ShowNoticeCard), nameof(ShowNoticeLine), nameof(ShowNoticeDurationLine),
            nameof(ShowDataSetting), nameof(ShowLogSetting), nameof(ShowCheckSetting), nameof(ShowDownloadSetting), nameof(ShowVersionCard),
            nameof(ShowAppearanceCard), nameof(ShowWindowCard), nameof(ShowSummonCard), nameof(ShowPhotoshopCard), nameof(ShowUpdateCard), nameof(ShowDataCard),
            nameof(ShowTrayEdgeLine), nameof(ShowEdgeDelayLine), nameof(ShowDelayStartupLine),
            nameof(ShowHotkeyProgramLine), nameof(ShowHotkeyProgramPointerLine), nameof(ShowPointerAnimationLine),
            nameof(ShowCheckDownloadLine), nameof(ShowDownloadVersionLine), nameof(ShowDataLogLine)
        }) OnPropertyChanged(name);
    }

    [RelayCommand] private void ClearSettingsSearch() => SettingsSearchQuery = "";

    public void OpenSettingsPage(bool showUpdates = false)
    {
        ReadSilentStartup();
        // Search is temporary navigation, not a remembered export setting.
        SettingsSearchQuery = "";
        IsViewSetting = true;
        SettingsNavigationRequested?.Invoke(showUpdates);
    }
}
