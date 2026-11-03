using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using PsExporter.Models;
using PsExporter.Core.Logging;
using PsExporter.Core.Process;
using PsExporter.Core.PsConnection;
using PsExporter.Services;
using PsExporter.ViewModels;
using PsExporter.Views;

namespace PsExporter;

public partial class App : Application
{
    private static AppThemeMode _themeMode = AppThemeMode.Light;

    public static SingleInstanceCoordinator? SingleInstance { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        ActualThemeVariantChanged += OnActualThemeVariantChanged;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        var provider = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow
            {
                DataContext = provider.GetRequiredService<MainViewModel>(),
                ScriptBridge = provider.GetRequiredService<UxpScriptBridge>(),
            };
            mainWindow.PrepareSavedPlacement();
            desktop.MainWindow = mainWindow;
            InitializeTray(desktop, mainWindow, provider.GetRequiredService<MainViewModel>(), provider.GetRequiredService<FileLogger>());
            SingleInstance?.StartListening(() =>
                Dispatcher.UIThread.Post(mainWindow.ActivateFromExternalRequest));
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static void ApplyTheme(AppThemeMode mode)
    {
        if (Current is not { } app) return;

        _themeMode = mode;
        app.RequestedThemeVariant = mode switch
        {
            AppThemeMode.Dark => ThemeVariant.Dark,
            AppThemeMode.Light => ThemeVariant.Light,
            _ => ThemeVariant.Default
        };
        ApplyThemeColors(app.ActualThemeVariant == ThemeVariant.Dark);
    }

    private static void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        if (_themeMode == AppThemeMode.System && Current is { } app)
            ApplyThemeColors(app.ActualThemeVariant == ThemeVariant.Dark);
    }

    public static bool IsDarkTheme(AppThemeMode mode, bool systemIsDark)
        => mode == AppThemeMode.Dark || (mode == AppThemeMode.System && systemIsDark);

    private static void ApplyThemeColors(bool isDark)
    {
        if (Current is not { } app) return;

        var colors = isDark
            ? new Dictionary<string, string>
            {
                ["Brush.SettingsCard"] = "#1B1F2A",
                ["Brush.SettingsBorder"] = "#323A4A",
                ["Brush.SettingsTrack"] = "#121620",
                ["Brush.Canvas"] = "#11131A",
                ["Brush.Surface"] = "#191C26",
                ["Brush.SurfaceSubtle"] = "#222632",
                ["Brush.Border"] = "#343A4A",
                ["Brush.Divider"] = "#2A2F3D",
                ["Brush.TextPrimary"] = "#F3F4FA",
                ["Brush.TextSecondary"] = "#B6BAC9",
                ["Brush.TextMuted"] = "#858B9D",
                ["Brush.TextFaint"] = "#9199AD",
                ["Brush.Hover"] = "#202D47",
                ["Brush.StatusSurface"] = "#182B4D",
                ["Brush.StatusBorder"] = "#2D5794",
                ["Brush.StatusText"] = "#73A5FF",
                ["Brush.ProgressTrack"] = "#30364A"
            }
            : new Dictionary<string, string>
            {
                ["Brush.SettingsCard"] = "#FFFFFF",
                ["Brush.SettingsBorder"] = "#E1E5EF",
                ["Brush.SettingsTrack"] = "#EEF1F6",
                ["Brush.Canvas"] = "#F4F5FA",
                ["Brush.Surface"] = "#FFFFFF",
                ["Brush.SurfaceSubtle"] = "#F8F9FD",
                ["Brush.Border"] = "#E2E4F0",
                ["Brush.Divider"] = "#ECEEF6",
                ["Brush.TextPrimary"] = "#141527",
                ["Brush.TextSecondary"] = "#5C5F79",
                ["Brush.TextMuted"] = "#73788A",
                ["Brush.TextFaint"] = "#74798B",
                ["Brush.Hover"] = "#EDF4FF",
                ["Brush.StatusSurface"] = "#EAF2FF",
                ["Brush.StatusBorder"] = "#C5D8FF",
                ["Brush.StatusText"] = "#1E65F2",
                ["Brush.ProgressTrack"] = "#EAEAFA"
            };

        foreach (var pair in colors)
        {
            if (app.TryGetResource(pair.Key, null, out var value) && value is SolidColorBrush brush)
                brush.Color = Color.Parse(pair.Value);
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // 运行日志与 EXE 放在同一目录，便于直接检查。
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var logDirectory = AppContext.BaseDirectory;
        Directory.CreateDirectory(logDirectory);
        var fileLogger = new FileLogger(Path.Combine(logDirectory, "psx.log"));
        fileLogger.Info($"启动 pid={Environment.ProcessId} exe={Environment.ProcessPath} version={typeof(App).Assembly.GetName().Version} log={fileLogger.LogPath}");
        services.AddSingleton(fileLogger);

        // PS 进程检测与 COM 桥接
        services.AddSingleton<IPhotoshopDetector, PhotoshopDetector>();
        services.AddSingleton<IPsBridge, WindowsComBridge>();
        services.AddSingleton<UxpScriptBridge>();

        // 业务服务
        services.AddSingleton<IPhotoshopService, PhotoshopService>();
        services.AddSingleton<IExportService, ExportService>();
        services.AddSingleton<ISettingsService, SettingsService>();

        // ViewModel
        services.AddSingleton<MainViewModel>();
    }
}
