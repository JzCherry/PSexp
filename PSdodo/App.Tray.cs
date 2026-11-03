using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using PsExporter.Core.Logging;
using PsExporter.Models;
using PsExporter.ViewModels;
using PsExporter.Views;

namespace PsExporter;

public partial class App
{
    private TrayIcon? _trayIcon;
    private MainWindow? _trayWindow;
    private MainViewModel? _trayModel;
    private FileLogger? _trayLogger;
    private NativeMenuItem? _trayQuickExport;
    private NativeMenuItem? _trayOpenFolder;
    private NativeMenuItem? _trayPin;
    private NativeMenuItem? _trayCheckUpdates;
    private NativeMenuItem? _trayExit;
    internal bool HasTrayIcon => _trayIcon is not null;
    internal bool IsExitRequested { get; private set; }

    private void InitializeTray(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, MainViewModel model, FileLogger logger)
    {
        _trayWindow = window;
        _trayModel = model;
        _trayLogger = logger;
        try
        {
            var menu = new NativeMenu();
            menu.Add(new NativeMenuItem("显示主窗口") { Command = new RelayCommand(() => window.ActivateFromExternalRequest()) });
            _trayQuickExport = new NativeMenuItem("快速导出当前画布") { Command = new RelayCommand(QuickExportFromTray) };
            menu.Add(_trayQuickExport);
            menu.Add(new NativeMenuItemSeparator());
            _trayOpenFolder = new NativeMenuItem("打开输出目录") { Command = model.OpenExportFolderCommand };
            menu.Add(_trayOpenFolder);
            menu.Add(new NativeMenuItem("打开日志目录") { Command = model.OpenLogDirectoryCommand });
            menu.Add(new NativeMenuItemSeparator());
            _trayPin = new NativeMenuItem("窗口置顶") { Command = model.ToggleAlwaysOnTopCommand, ToggleType = MenuItemToggleType.CheckBox };
            menu.Add(_trayPin);
            menu.Add(new NativeMenuItem("设置") { Command = new RelayCommand(() => ShowTraySettings(false)) });
            _trayCheckUpdates = new NativeMenuItem("检查更新") { Command = new RelayCommand(CheckUpdatesFromTray) };
            menu.Add(_trayCheckUpdates);
            menu.Add(new NativeMenuItemSeparator());
            _trayExit = new NativeMenuItem("退出 PSexp")
            {
                Command = new RelayCommand(RequestExit),
                ToolTip = "导出期间请先等待完成，或在主窗口停止任务。"
            };
            menu.Add(_trayExit);
            // Avalonia names the Win32 tray popup from this tooltip; keep it stable
            // to match the narrowly scoped continuous-corner theme in Styles/Tray.axaml.
            _trayIcon = new TrayIcon { Icon = window.Icon, ToolTipText = "PSexp", Menu = menu, IsVisible = true };
            _trayIcon.Clicked += OnTrayIconClicked;
            TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            model.PropertyChanged += OnTrayModelChanged;
            desktop.Exit += (_, _) => DisposeTray();
            window.Closed += (_, _) => Dispatcher.UIThread.Post(RequestExit);
            RefreshTrayMenu();
            logger.Info("托盘已启用；单击显示主窗口，右键打开菜单。");
        }
        catch (Exception error)
        {
            DisposeTray();
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            logger.Error("托盘初始化失败，关闭窗口将正常退出：" + error);
        }
    }

    private void OnTrayIconClicked(object? sender, EventArgs e) => _trayWindow?.ActivateFromExternalRequest();

    private void OnTrayModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.CanExport) or nameof(MainViewModel.IsBusy)
            or nameof(MainViewModel.IsExporting) or nameof(MainViewModel.IsAlwaysOnTop)
            or nameof(MainViewModel.CanOpenExportFolder) or nameof(MainViewModel.CanCheckUpdates))
        {
            if (Dispatcher.UIThread.CheckAccess()) RefreshTrayMenu();
            else Dispatcher.UIThread.Post(RefreshTrayMenu);
        }
    }

    private void RefreshTrayMenu()
    {
        if (_trayModel is not { } model) return;
        if (_trayQuickExport is { } quick)
            quick.IsEnabled = !model.IsBusy && !model.IsRefreshingStatus && model.IsPsRunning
                && model.IsBridgeAvailable && model.HasDocument && model.HasExportTarget;
        if (_trayOpenFolder is { } folder) folder.IsEnabled = model.CanOpenExportFolder;
        if (_trayPin is { } pin) pin.IsChecked = model.IsAlwaysOnTop;
        if (_trayCheckUpdates is { } updates) updates.IsEnabled = model.CanCheckUpdates;
        if (_trayExit is { } exit) exit.IsEnabled = !model.IsBusy && !model.IsExporting;
    }

    private void QuickExportFromTray()
    {
        if (_trayModel is not { } model || _trayWindow is not { } window) return;
        model.IsViewSetting = false;
        model.SelectedMode = ExportMode.QuickExport;
        // Start the export's fresh document probe before activating the window. Otherwise
        // activation starts its own probe and temporarily disables the export command.
        if (model.StartExportCommand.CanExecute(null)) model.StartExportCommand.Execute(null);
        window.ActivateFromExternalRequest();
    }

    private void ShowTraySettings(bool updates)
    {
        if (_trayWindow is not { } window || _trayModel is not { } model) return;
        window.ActivateFromExternalRequest();
        model.OpenSettingsPage(updates);
    }

    private void CheckUpdatesFromTray()
    {
        ShowTraySettings(true);
        if (_trayModel?.CheckForUpdatesCommand.CanExecute(null) == true)
            _trayModel.CheckForUpdatesCommand.Execute(null);
    }

    internal void NotifyHiddenToTray() => _trayLogger?.Info("主窗口已收起到托盘；后台任务继续运行。");

    internal void MarkShutdownRequested() => IsExitRequested = true;

    internal async void RequestExit()
    {
        if (IsExitRequested) return;
        IsExitRequested = true;
        _trayLogger?.Info("正在退出 PSexp。");
        if (_trayModel is { } model)
        {
            try { await model.StopDocumentEventsAsync().WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (Exception error) { _trayLogger?.Debug("事件清理等待结束；租约已失效：" + error.Message); }
        }
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }

    private void DisposeTray()
    {
        if (_trayModel is { } model) model.PropertyChanged -= OnTrayModelChanged;
        if (_trayIcon is { } icon)
        {
            icon.Clicked -= OnTrayIconClicked;
            var registered = TrayIcon.GetIcons(this)?.Contains(icon) == true;
            TrayIcon.SetIcons(this, null); // The application-owned collection disposes the native icon.
            if (!registered) icon.Dispose();
            _trayIcon = null;
        }
        _trayModel = null;
        _trayWindow = null;
    }
}
