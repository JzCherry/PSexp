using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PsExporter.Services;

namespace PsExporter.ViewModels;

public partial class MainViewModel
{
    private UpdateService? _updater;
    private UpdateManifest? _pendingUpdate;
    private string? _downloadedUpdate;
    private string? _automaticUpdateVersion;
    private readonly CancellationTokenSource _updateLifetime = new();
    private bool _updatesStarted;
    [ObservableProperty] private bool _autoCheckForUpdates = true;
    [ObservableProperty] private bool _autoDownloadUpdates;
    [ObservableProperty] private bool _isUpdateBusy;
    [ObservableProperty] private bool _isInstallingUpdate;
    [ObservableProperty] private double _updateProgress;
    [ObservableProperty] private string _updateStatus = AppProfile.IsTestBuild
        ? "测试版请通过编译脚本更新"
        : "启动后自动检查更新";
    [ObservableProperty] private string _updateChangelog = "";
    public bool HasUpdateChangelog => !string.IsNullOrWhiteSpace(UpdateChangelog);
    partial void OnUpdateChangelogChanged(string value) => OnPropertyChanged(nameof(HasUpdateChangelog));
    public string UpdateVersionLabel => "v" + UpdateService.CurrentVersion;
    public string UpdateCurrentVersion => "当前版本：" + UpdateService.CurrentVersion;
    public string UpdateActionText => _downloadedUpdate is null ? "下载并更新" : "重启并更新";
    public bool IsUpdateAvailable => _pendingUpdate is not null;
    internal string? AutomaticUpdateVersion => UpdateService.IsSingleFileInstallation
        && AutoCheckForUpdates && CanApplyUpdate && _pendingUpdate?.Version == _automaticUpdateVersion
        ? _automaticUpdateVersion : null;
    internal bool IsUpdateDownloaded => _downloadedUpdate is not null;
    public string SettingsToggleHint => IsViewSetting ? "返回导出界面"
        : IsUpdateAvailable ? "发现新版本，进入设置更新" : "设置";
    public bool CanCheckUpdates => !AppProfile.IsTestBuild && !IsUpdateBusy;
    public bool CanApplyUpdate => !IsBusy && !IsExporting && !IsUpdateBusy && _pendingUpdate is not null;
    private UpdateService Updater => _updater ??= new UpdateService(_fileLogger);
    partial void OnAutoCheckForUpdatesChanged(bool value) => SaveBehaviorSettings();
    partial void OnAutoDownloadUpdatesChanged(bool value) => SaveBehaviorSettings();
    partial void OnIsUpdateBusyChanged(bool value) => RefreshUpdateCommands();
    [RelayCommand] private void SetAutoCheckForUpdates(object? value) => AutoCheckForUpdates = ParseBooleanCommandParameter(value);
    [RelayCommand] private void SetAutoDownloadUpdates(object? value) => AutoDownloadUpdates = ParseBooleanCommandParameter(value);
    private void RefreshUpdateCommands()
    {
        OnPropertyChanged(nameof(CanApplyUpdate));
        OnPropertyChanged(nameof(IsUpdateAvailable));
        OnPropertyChanged(nameof(SettingsToggleHint));
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
        ApplyUpdateCommand.NotifyCanExecuteChanged();
    }
    public async Task InitializeUpdatesAsync()
    {
        if (AppProfile.IsTestBuild || _updatesStarted) return;
        _updatesStarted = true;
        try
        {
            await Task.Delay(3000, _updateLifetime.Token);
            while (!_updateLifetime.IsCancellationRequested)
            {
                if (AutoCheckForUpdates && !IsUpdateBusy) await CheckUpdateCoreAsync(automatic: true);
                await Task.Delay(TimeSpan.FromHours(6), _updateLifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
    }
    public void StopUpdateChecks() => _updateLifetime.Cancel();
    [RelayCommand(CanExecute = nameof(CanCheckUpdates))]
    private Task CheckForUpdatesAsync() => CheckUpdateCoreAsync();
    private async Task CheckUpdateCoreAsync(bool automatic = false)
    {
        if (!CanCheckUpdates) return;
        IsUpdateBusy = true;
        UpdateProgress = 0;
        UpdateStatus = "正在检查更新…";
        try
        {
            var info = await Updater.CheckAsync(_updateLifetime.Token);
            if (!UpdateService.IsNewer(info.Version))
            {
                _pendingUpdate = null;
                _downloadedUpdate = null;
                _automaticUpdateVersion = null;
                UpdateChangelog = "";
                UpdateStatus = "已是最新版本";
                return;
            }
            if (_pendingUpdate?.Sha256 != info.Sha256 || !File.Exists(_downloadedUpdate)) _downloadedUpdate = null;
            _pendingUpdate = info;
            if (automatic) _automaticUpdateVersion = info.Version;
            UpdateChangelog = info.Changelog;
            UpdateStatus = "发现新版本 v" + info.Version;
            if (AutoDownloadUpdates && UpdateService.IsSingleFileInstallation && _downloadedUpdate is null)
                await DownloadAvailableUpdateAsync();
            if (_downloadedUpdate is not null) UpdateStatus = "v" + info.Version + " 已就绪，点击重启并更新";
        }
        catch (OperationCanceledException) when (_updateLifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            UpdateStatus = "检查更新失败：" + error.Message;
            _fileLogger.Error("更新检查失败：" + error);
        }
        finally
        {
            IsUpdateBusy = false;
            OnPropertyChanged(nameof(UpdateActionText));
            RefreshUpdateCommands();
        }
    }
    private async Task DownloadAvailableUpdateAsync()
    {
        if (_pendingUpdate is null) return;
        if (!UpdateService.IsSingleFileInstallation) throw new IOException("目录开发版请使用编译脚本更新，自动安装仅支持发布版 EXE");
        UpdateProgress = 0;
        UpdateStatus = "正在下载 v" + _pendingUpdate.Version + "…";
        _downloadedUpdate = await Updater.DownloadAsync(_pendingUpdate,
            new Progress<double>(value => UpdateProgress = value), _updateLifetime.Token);
    }
    [RelayCommand(CanExecute = nameof(CanApplyUpdate))]
    private async Task ApplyUpdateAsync()
    {
        if (_pendingUpdate is null || IsBusy || IsExporting || IsUpdateBusy) return;
        IsUpdateBusy = true;
        try
        {
            if (_downloadedUpdate is null) await DownloadAvailableUpdateAsync();
            if (IsBusy || IsExporting)
            {
                UpdateStatus = "更新已下载，导出结束后可重启安装";
                return;
            }
            IsInstallingUpdate = true;
            Updater.BeginInstall(_pendingUpdate, _downloadedUpdate!);
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }
        catch (OperationCanceledException) when (_updateLifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            UpdateStatus = "更新未安装：" + error.Message;
            _fileLogger.Error("更新安装失败：" + error);
        }
        finally
        {
            IsInstallingUpdate = false;
            IsUpdateBusy = false;
            OnPropertyChanged(nameof(UpdateActionText));
            RefreshUpdateCommands();
        }
    }
}
