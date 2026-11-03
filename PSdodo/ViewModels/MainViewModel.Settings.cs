using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PsExporter.Models;

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

    internal event Action<string, int>? ExportCompleted;

    [ObservableProperty] private bool _closeToTray = true;
    public string WindowCloseHint => CloseToTray ? "收起到托盘，后台继续运行" : "退出 PSexp";
    partial void OnCloseToTrayChanged(bool value)
    {
        OnPropertyChanged(nameof(WindowCloseHint));
        SaveBehaviorSettings();
    }
    [RelayCommand] private void SetCloseToTray(object? value) => CloseToTray = ParseBooleanCommandParameter(value);

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
