using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PsExporter.Models;
using PsExporter.Services;

namespace PsExporter.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] private bool _showExportNotice = true;
    [ObservableProperty] private int _exportNoticeDurationSeconds;
    [ObservableProperty] private ExportNoticeTarget _exportNoticeTarget;

    partial void OnShowExportNoticeChanged(bool value)
    {
        SaveBehaviorSettings();
        RefreshSettingsSearch();
    }
    partial void OnExportNoticeDurationSecondsChanged(int value) => SaveBehaviorSettings();
    partial void OnExportNoticeTargetChanged(ExportNoticeTarget value) => SaveBehaviorSettings();

    [RelayCommand] private void SetShowExportNotice(object? value) => ShowExportNotice = ParseBooleanCommandParameter(value);
    [RelayCommand] private void SetExportNoticeDuration(object? value)
    {
        if (int.TryParse(value?.ToString(), out var seconds) && IsExportNoticeDuration(seconds))
            ExportNoticeDurationSeconds = seconds;
    }
    private static bool IsExportNoticeDuration(int seconds) => seconds is 3 or AppSettings.DefaultExportNoticeDurationSeconds or 10;
    [RelayCommand] private void SetExportNoticeTarget(ExportNoticeTarget value) => ExportNoticeTarget = value;

    internal event Action<string, int?>? ExportStarted;
    internal event Action<string, int>? ExportCompleted;
    internal event Action<bool>? ExportEndedWithoutResult;

    [ObservableProperty] private bool _closeToTray = true;
    public string WindowCloseHint => CloseToTray ? "收起到托盘，后台继续运行" : "退出 PSexp";
    partial void OnCloseToTrayChanged(bool value)
    {
        OnPropertyChanged(nameof(WindowCloseHint));
        SaveBehaviorSettings();
    }
    [RelayCommand] private void SetCloseToTray(object? value) => CloseToTray = ParseBooleanCommandParameter(value);

    [ObservableProperty] private bool _silentStartupEnabled;

    private void ReadSilentStartup()
    {
        try { SilentStartupEnabled = WindowsStartup.IsEnabled(); }
        catch (Exception error) { _fileLogger.Error("读取开机启动失败：" + error); }
    }

    [RelayCommand]
    private void SetSilentStartup(object? value)
    {
        var enabled = ParseBooleanCommandParameter(value);
        try
        {
            WindowsStartup.SetEnabled(enabled);
            SilentStartupEnabled = enabled;
            SettingsStatusText = enabled ? "已开启开机静默启动" : "已关闭开机静默启动";
        }
        catch (Exception error)
        {
            SettingsStatusText = "无法更改开机启动：" + error.Message;
            _fileLogger.Error("设置开机启动失败：" + error);
        }
    }

    // ---------- 全局唤起热键 ----------
    [ObservableProperty] private string _summonHotkey = AppSettings.DefaultSummonHotkey;
    [ObservableProperty] private string _summonHotkeyProgram = AppSettings.DefaultSummonHotkeyProgram;
    [ObservableProperty] private bool _isSummonHotkeyCapturing;
    [ObservableProperty] private PointerSummonMode _summonPointerMode;
    [ObservableProperty] private bool _summonReturnAnimation;

    public bool FollowsPointerOnSummon => SummonPointerMode != PointerSummonMode.Off;

    /// <summary>窗口层订阅后重新注册系统热键；设置项本身只负责落盘与显示。</summary>
    public event Action? SummonHotkeyRegistrationRequested;

    /// <summary>空组合键就是关闭：不注册任何系统热键，界面上显示「未设置」。</summary>
    public string SummonHotkeyDisplay => IsSummonHotkeyCapturing ? "按键…"
        : SummonHotkey.Length == 0 ? "未设置" : SummonHotkey;
    public string SummonHotkeyProgramDisplay => SummonHotkeyProgram.Length == 0 ? "未绑定"
        : Path.GetFileNameWithoutExtension(SummonHotkeyProgram);

    partial void OnSummonHotkeyChanged(string value)
    {
        OnPropertyChanged(nameof(SummonHotkeyDisplay));
        SaveBehaviorSettings();
        SummonHotkeyRegistrationRequested?.Invoke();
    }

    partial void OnIsSummonHotkeyCapturingChanged(bool value) => OnPropertyChanged(nameof(SummonHotkeyDisplay));

    partial void OnSummonHotkeyProgramChanged(string value)
    {
        OnPropertyChanged(nameof(SummonHotkeyProgramDisplay));
        OnPropertyChanged(nameof(HasSummonHotkeyProgram));
        SaveBehaviorSettings();
        SummonHotkeyRegistrationRequested?.Invoke();
    }

    public bool HasSummonHotkeyProgram => SummonHotkeyProgram.Length > 0;
    [RelayCommand] private void ClearSummonHotkeyProgram() => SummonHotkeyProgram = "";


    partial void OnSummonPointerModeChanged(PointerSummonMode value)
    {
        SaveBehaviorSettings();
        RefreshSettingsSearch();
    }

    partial void OnSummonReturnAnimationChanged(bool value) => SaveBehaviorSettings();

    [RelayCommand] private void SetSummonPointerMode(PointerSummonMode value) => SummonPointerMode = value;
    [RelayCommand] private void SetSummonReturnAnimation(object? value) => SummonReturnAnimation = ParseBooleanCommandParameter(value);

    public string LogFilePath => _fileLogger.LogPath;
    [RelayCommand] private void OpenLogDirectory()
    {
        try
        {
            var directory = Path.GetDirectoryName(LogFilePath)!;
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception error) { AppendLog("Error", "打开日志目录失败：" + error.Message); }
    }
}
