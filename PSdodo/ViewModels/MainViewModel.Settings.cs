using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PsExporter.ViewModels;

public partial class MainViewModel
{
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
