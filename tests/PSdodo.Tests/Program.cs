using PsExporter;
using PsExporter.Core.Automation;
using PsExporter.Core.PsConnection;
using PsExporter.Core.Scripting;
using PsExporter.Models;
using PsExporter.Services;
using PsExporter.ViewModels;
using PsExporter.Views;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using System.Runtime.InteropServices;

var failures = new List<string>();

Run("JavaScript string escaping", () =>
{
    var escaped = ScriptParameterInjector.EscapeJsString("C:\\Bob's\nfile");
    Equal("C:\\\\Bob\\'s\\nfile", escaped);
});

Run("JavaScript escaping protects control characters and UTF-16", () =>
{
    Equal("\\u0000\\u0009\\u0085\\u2028\\u2029\\ufffd\\ud800\\ud83d\\ude00",
        ScriptParameterInjector.EscapeJsString("\0\t\u0085\u2028\u2029\ufffd\ud800😀"));
});

Run("Export names preserve Unicode and normalize unsupported characters", () =>
{
    Equal("海报__😀___", ExportName.Normalize("海报\r\n😀\u0001\ufffd\ud800"));
    Equal("_CON", ExportName.Normalize("CON"));
    Equal("_nul.png", ExportName.Normalize("nul.png"));
    Equal("未命名", ExportName.Normalize("..."));
    Equal("中文👩‍💻", ExportName.Normalize("中文👩‍💻"));
});

Run("Export settings reject relative paths", () =>
{
    var settings = new ExportSettings { ExportPath = "relative-output" };
    True(!ExportSettingsValidator.TryPrepare(settings, out _));
});

Run("Windows export commands round-trip", () =>
{
    const string executable = @"C:\方向 2 台机\PS exp\PSexp v3.0.6.exe";
    Equal(Enum.GetValues<ExportInvocationKind>().Length, ExportInvocation.Forms.Count);
    Equal($"\"{executable}\" --preset-id 08a489ffd20b4accb4bcb4604fe304f9",
        ExportInvocation.BuildWindowsCommandLine(executable, ExportInvocation.ForPresetId("08a489ffd20b4accb4bcb4604fe304f9")));
    if (!OperatingSystem.IsWindows()) return;

    foreach (var form in ExportInvocation.Forms)
        CheckRoundTrip(SampleInvocation(form.Kind));
    CheckRoundTrip(ExportInvocation.ForPresetName(ExportPresetCategory.Layers, "Bob's \"图层\" \\\" #2\\"));

    void CheckRoundTrip(ExportInvocation sample)
    {
        var command = ExportInvocation.BuildWindowsCommandLine(executable, sample);
        var tokens = SplitWindowsCommandLine(command);
        Equal(executable, tokens[0]);
        var accepted = ExportInvocation.TryParse(tokens[1..], out var parsed);
        True(accepted && parsed == sample, $"Windows 解析后的参数未能还原指令：{command}");
    }
});

var tempRoot = Path.Combine(Path.GetTempPath(), $"ps-exporter-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(tempRoot);

try
{
    Run("Build profile selects isolated or production directories and startup permissions", () =>
    {
        var isTestBuild = args.Contains("--test-profile");
        Equal(isTestBuild, AppProfile.IsTestBuild);
        Equal(isTestBuild ? "PsExporter-Test" : "PsExporter", Path.GetFileName(new SettingsService().DataDirectory));
        Equal(isTestBuild ? "PSexp-Test" : "PSexp", Path.GetFileName(AppProfile.LocalDirectory));
        Equal(isTestBuild ? "PSexpV3.Test" : "PSexpV3", AppProfile.InstanceId);
        Equal(OperatingSystem.IsWindows() && !isTestBuild, WindowsStartup.CanConfigure);
        var freshSettings = new SettingsService(Path.Combine(tempRoot, "new-profile", "settings.json")).Load();
        Equal(isTestBuild ? "" : AppSettings.DefaultSummonHotkey, freshSettings.SummonHotkey);
    });

    Run("Startup registration resolves the existing executable and rejects stale commands", () =>
    {
        var executable = Path.Combine(tempRoot, "PSexp v3.0.exe");
        var command = WindowsStartup.CommandFor(executable);
        File.WriteAllText(executable, "");
        Equal(executable, WindowsStartup.GetRegisteredExecutable(command));
        Equal<string?>(null, WindowsStartup.GetRegisteredExecutable($"\" {WindowsStartup.Argument}"));
        Equal<string?>(null, WindowsStartup.GetRegisteredExecutable($"\"{executable}\" --other"));
        File.Delete(executable);
        Equal<string?>(null, WindowsStartup.GetRegisteredExecutable(command));
    });

    Run("Saving a fresh settings snapshot preserves fields from newer versions", () =>
    {
        var settingsPath = Path.Combine(tempRoot, "future-settings.json");
        File.WriteAllText(settingsPath, """{"defaultQuality":80,"futureSettings":{"enabled":true},"futureNullable":null}""");
        var service = new SettingsService(settingsPath);
        Equal(80, service.Load().DefaultQuality);
        True(service.Save(new AppSettings { DefaultQuality = 60 }));
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath));
        var json = document.RootElement;
        Equal(60, json.GetProperty("defaultQuality").GetInt32());
        True(json.GetProperty("futureSettings").GetProperty("enabled").GetBoolean());
        Equal(System.Text.Json.JsonValueKind.Null, json.GetProperty("futureNullable").ValueKind);
    });

    Run("Export settings prepare writable directory", () =>
    {
        var exportPath = Path.Combine(tempRoot, "output");
        var settings = new ExportSettings { ExportPath = exportPath, Quality = 80 };
        True(ExportSettingsValidator.TryPrepare(settings, out var error), error);
        Equal(Path.GetFullPath(exportPath), settings.ExportPath);
        True(Directory.Exists(exportPath));
    });

    Run("Settings save and reload atomically", () =>
    {
        var settingsPath = Path.Combine(tempRoot, "settings", "settings.json");
        var service = new SettingsService(settingsPath);
        var expected = new AppSettings
        {
            DefaultExportPath = Path.Combine(tempRoot, "exports"),
            DefaultFormat = ExportFormat.Png,
            DefaultQuality = 72,
            DefaultAutoRename = false,
            UseDocumentNameAsFallbackPrefix = true,
            LayerExportWithDocName = false,
            LayerExportShowHint = true,
            AutoUseDocNameAsPrefix = true,
            ThemeMode = AppThemeMode.Dark,
            ExportHighlights = new() { [ExportMode.AllArtboards] = ExportHighlight.Violet, [ExportMode.Slices] = ExportHighlight.Sky },
            StartAlwaysOnTop = true,
            AutoRefreshOnActivated = false,
            SummonHotkey = "Ctrl+Alt+Shift+F12",
            SummonHotkeyProgram = Path.Combine(tempRoot, "Example.exe"),
            SummonAtPointer = true,
            SummonPointerMode = PointerSummonMode.Center,
            SummonReturnAnimation = true,
            WindowWidth = 1120,
            WindowHeight = 880
        };

        True(service.Save(expected));
        var loaded = new SettingsService(settingsPath).Load();
        Equal(expected.DefaultExportPath, loaded.DefaultExportPath);
        Equal(expected.DefaultFormat, loaded.DefaultFormat);
        Equal(expected.DefaultQuality, loaded.DefaultQuality);
        Equal(expected.DefaultAutoRename, loaded.DefaultAutoRename);
        Equal(expected.UseDocumentNameAsFallbackPrefix, loaded.UseDocumentNameAsFallbackPrefix);
        Equal(expected.LayerExportWithDocName, loaded.LayerExportWithDocName);
        Equal(expected.LayerExportShowHint, loaded.LayerExportShowHint);
        Equal(expected.AutoUseDocNameAsPrefix, loaded.AutoUseDocNameAsPrefix);
        Equal(expected.ThemeMode, loaded.ThemeMode);
        Equal(ExportHighlight.Violet, loaded.ExportHighlights[ExportMode.AllArtboards]);
        Equal(ExportHighlight.Sky, loaded.ExportHighlights[ExportMode.Slices]);
        Equal(expected.StartAlwaysOnTop, loaded.StartAlwaysOnTop);
        Equal(expected.AutoRefreshOnActivated, loaded.AutoRefreshOnActivated);
        Equal(expected.SummonHotkey, loaded.SummonHotkey);
        Equal(expected.SummonHotkeyProgram, loaded.SummonHotkeyProgram);
        Equal(expected.SummonAtPointer, loaded.SummonAtPointer);
        Equal(PointerSummonMode.Center, loaded.SummonPointerMode);
        Equal(true, loaded.SummonReturnAnimation);
        Equal(expected.WindowWidth, loaded.WindowWidth);
        Equal(expected.WindowHeight, loaded.WindowHeight);

        // 空组合键＝关闭，落盘后不能被悄悄写回默认值。
        var cleared = new AppSettings
        {
            DefaultExportPath = expected.DefaultExportPath,
            SummonHotkey = "",
            SummonHotkeyProgram = "",
            SummonAtPointer = false
        };
        True(service.Save(cleared));
        var reloaded = new SettingsService(settingsPath).Load();
        Equal("", reloaded.SummonHotkey);
        Equal("", reloaded.SummonHotkeyProgram);
        Equal(false, reloaded.SummonAtPointer);
        Equal(PointerSummonMode.Off, reloaded.SummonPointerMode);
        Equal(false, reloaded.SummonReturnAnimation);
        Equal(0, reloaded.ExportHighlights.Count);

        File.WriteAllText(settingsPath, """{"summonAtPointer":true}""");
        Equal(PointerSummonMode.Automatic, service.Load().SummonPointerMode);
        Equal(AppSettings.DefaultSummonHotkeyProgram, service.Current.SummonHotkeyProgram);
        File.WriteAllText(settingsPath, """{"summonPointerMode":0,"summonAtPointer":true}""");
        Equal(PointerSummonMode.Off, service.Load().SummonPointerMode);
    });

    Run("Export highlight colors remain readable in both themes", () =>
    {
        Equal(ExportHighlight.Blue, ExportHighlightSetting.Palette[0].Value);
        foreach (var choice in ExportHighlightSetting.Palette)
        {
            var color = ((ISolidColorBrush)choice.Fill).Color;
            True(Contrast(color, Colors.White) >= 4.5, choice.Name + " 的白色徽标文字对比度不足");
            foreach (var state in new[] { choice.LightInk, choice.PressedFill })
                True(Contrast(((ISolidColorBrush)state).Color, Colors.White) >= 4.5, choice.Name + " 的导出按钮白字对比度不足");
            foreach (var surface in new[] { "#F8F9FD", "#1B1F2A", "#1F232C" })
                True(Contrast(color, Color.Parse(surface)) >= 3, choice.Name + " 的边框或色点对比度不足：" + surface);
            var swatch = ((ISolidColorBrush)choice.Swatch).Color;
            True(Contrast(swatch, Color.Parse("#EEF1F6")) >= 3, choice.Name + " 的浅色轨道色块对比度不足");
            True(Contrast(swatch, Color.Parse("#121620")) >= 3, choice.Name + " 的深色轨道色块对比度不足");
        }

        static double Contrast(Color a, Color b)
        {
            static double Linear(byte channel)
            {
                var srgb = channel / 255d;
                return srgb <= 0.04045 ? srgb / 12.92 : Math.Pow((srgb + 0.055) / 1.055, 2.4);
            }
            static double Luminance(Color color) =>
                0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
            var left = Luminance(a);
            var right = Luminance(b);
            return (Math.Max(left, right) + 0.05) / (Math.Min(left, right) + 0.05);
        }
    });

    Run("User window size persists without changing its aspect ratio", () =>
    {
        var settingsPath = Path.Combine(tempRoot, "custom-window-settings", "settings.json");
        var service = new SettingsService(settingsPath);
        True(service.Save(new AppSettings
        {
            DefaultExportPath = tempRoot,
            WindowWidth = 725,
            WindowHeight = 635
        }));

        var loaded = new SettingsService(settingsPath).Load();
        Equal(725d, loaded.WindowWidth);
        Equal(635d, loaded.WindowHeight);
        True(Math.Abs(loaded.WindowWidth / loaded.WindowHeight - AppSettings.WindowAspectRatio) > 0.1);
    });

    Run("Scroll fade directions respect viewport edges", () =>
    {
        Equal((false, true), MainWindow.GetScrollFadeDirections(0, 100, 300));
        Equal((true, true), MainWindow.GetScrollFadeDirections(80, 100, 300));
        Equal((true, false), MainWindow.GetScrollFadeDirections(200, 100, 300));
        Equal((false, false), MainWindow.GetScrollFadeDirections(0, 100, 80));
    });

    Run("System theme resolves actual light and dark mode", () =>
    {
        True(App.IsDarkTheme(AppThemeMode.Dark, systemIsDark: false));
        True(!App.IsDarkTheme(AppThemeMode.Light, systemIsDark: true));
        True(App.IsDarkTheme(AppThemeMode.System, systemIsDark: true));
        True(!App.IsDarkTheme(AppThemeMode.System, systemIsDark: false));
        Equal(0, (int)AppThemeMode.Light);
        Equal(1, (int)AppThemeMode.Dark);
        Equal(2, (int)AppThemeMode.System);
    });

    Run("Invalid and compact window dimensions normalize independently", () =>
    {
        Equal(440d, AppSettings.DefaultWindowWidth);
        Equal(660d, AppSettings.DefaultWindowHeight);
        Equal(360d, AppSettings.MinimumWindowWidth);
        Equal(600d, AppSettings.MinimumWindowHeight);
        Equal(0.75d, AppSettings.WindowAspectRatio);

        Equal(
            (AppSettings.DefaultWindowWidth, 640d),
            SettingsService.NormalizeWindowSize(double.NaN, 640));

        var minimum = SettingsService.NormalizeWindowSize(300, 400);
        Equal(360d, minimum.Width);
        Equal(600d, minimum.Height);

        var customSize = SettingsService.NormalizeWindowSize(725, 515);
        Equal(725d, customSize.Width);
        Equal(600d, customSize.Height);

        var previousSize = SettingsService.NormalizeWindowSize(450, 600);
        Equal(450d, previousSize.Width);
        Equal(600d, previousSize.Height);
    });

    Run("Photoshop status text remains stable without a document", () =>
    {
        Equal("Photoshop 未启动", MainViewModel.FormatDocumentDisplayName(
            isPsRunning: false, hasDocument: false, documentName: string.Empty));
        Equal("未打开 Photoshop 文档", MainViewModel.FormatDocumentDisplayName(
            isPsRunning: true, hasDocument: false, documentName: string.Empty));
        Equal("商品主图.psd", MainViewModel.FormatDocumentDisplayName(
            isPsRunning: true, hasDocument: true, documentName: "商品主图.psd"));
        Equal("未连接", MainViewModel.FormatPsVersionDisplay(false, string.Empty));
        Equal("26.0", MainViewModel.FormatPsVersionDisplay(true, "26.0"));
        Equal("无尺寸数据", MainViewModel.FormatDocumentSizeDisplay(false, string.Empty));
        Equal("1920 × 1080", MainViewModel.FormatDocumentSizeDisplay(true, "1920 × 1080"));
    });

    Run("Status animations follow real state and settle safely", () =>
    {
        var stoppedPulse = MainWindow.CalculatePsStatusPulse(false, 800);
        Equal(1d, stoppedPulse.Opacity);
        Equal(1d, stoppedPulse.Scale);

        var runningLow = MainWindow.CalculatePsStatusPulse(true, 0);
        var runningHigh = MainWindow.CalculatePsStatusPulse(true, 800);
        True(runningLow.Opacity < runningHigh.Opacity);
        True(runningLow.Scale < runningHigh.Scale);

        Equal(0d, MainWindow.CalculateRefreshRotation(false, 350));
        Equal(0d, MainWindow.CalculateRefreshRotation(true, 0));
        Equal(180d, MainWindow.CalculateRefreshRotation(true, 350));
        Equal(360d, MainWindow.CalculateRefreshRotation(true, 700));
        Equal(360d, MainWindow.CalculateRefreshRotation(true, 5_000));

        var shortRefreshAngle = MainWindow.CalculateRefreshRotation(true, 175);
        Equal(90d, shortRefreshAngle);
        Equal(90d, MainWindow.CalculateRefreshSettleRotation(shortRefreshAngle, 0));
        True(MainWindow.CalculateRefreshSettleRotation(shortRefreshAngle, 70) < shortRefreshAngle);
        Equal(0d, MainWindow.CalculateRefreshSettleRotation(shortRefreshAngle, 140));
        True(!MainWindow.IsRefreshSettleComplete(139));
        True(MainWindow.IsRefreshSettleComplete(140));
    });

    Run("Window size restores each dimension within screen working area", () =>
    {
        var normal = MainWindow.CalculateWindowConstraints(725, 515, 1920, 1040);
        Equal(725d, normal.Width);
        Equal(600d, normal.Height);
        Equal(360d, normal.MinWidth);
        Equal(600d, normal.MinHeight);
        Equal(1888d, normal.MaxWidth);
        Equal(1008d, normal.MaxHeight);

        var compact = MainWindow.CalculateWindowConstraints(300, 400, 1920, 1040);
        Equal(360d, compact.Width);
        Equal(600d, compact.Height);

        var smallScreen = MainWindow.CalculateWindowConstraints(725, 515, 640, 480);
        Equal(608d, smallScreen.Width);
        Equal(448d, smallScreen.Height);
        Equal(360d, smallScreen.MinWidth);
        Equal(448d, smallScreen.MinHeight);

        var tinyScreen = MainWindow.CalculateWindowConstraints(725, 515, 400, 300);
        Equal(368d, tinyScreen.Width);
        Equal(268d, tinyScreen.Height);
        Equal(360d, tinyScreen.MinWidth);
        Equal(268d, tinyScreen.MinHeight);
    });

    Run("Boolean setting command parameters parse XAML strings", () =>
    {
        True(MainViewModel.ParseBooleanCommandParameter("True"));
        True(!MainViewModel.ParseBooleanCommandParameter("False"));
        True(MainViewModel.ParseBooleanCommandParameter(true));
        True(!MainViewModel.ParseBooleanCommandParameter(null));
    });

    Run("Activation refresh honors guards and suppression", () =>
    {
        True(MainViewModel.CanRefreshOnActivation(
            enabled: true, isBusy: false, isRefreshing: false, suppressNextActivation: false));
        True(!MainViewModel.CanRefreshOnActivation(
            enabled: false, isBusy: false, isRefreshing: false, suppressNextActivation: false));
        True(!MainViewModel.CanRefreshOnActivation(
            enabled: true, isBusy: true, isRefreshing: false, suppressNextActivation: false));
        True(!MainViewModel.CanRefreshOnActivation(
            enabled: true, isBusy: false, isRefreshing: true, suppressNextActivation: false));
        True(!MainViewModel.CanRefreshOnActivation(
            enabled: true, isBusy: false, isRefreshing: false, suppressNextActivation: true));
    });

    Run("Layer image name follows configured priority", () =>
    {
        Equal("手动前缀", MainViewModel.ResolveLayerImageName(
            ExportMode.Layers, "手动前缀", "文档.psd", autoUseDocumentName: false));
        Equal("文档", MainViewModel.ResolveLayerImageName(
            ExportMode.Layers, "手动前缀", "文档.psd", autoUseDocumentName: true));
        Equal("快速名", MainViewModel.ResolveLayerImageName(
            ExportMode.QuickExport, "快速名", "文档.psd", autoUseDocumentName: true));
        Equal(string.Empty, MainViewModel.ResolveLayerImageName(
            ExportMode.Slices, "手动前缀", "文档.psd", autoUseDocumentName: true));
    });

    Run("Batch export uses the document prefix only when enabled and the input is blank", () =>
    {
        Equal("手动前缀", ExportFilePrefix.Resolve(" 手动前缀 ", "文档.psd", useDocumentName: true));
        Equal("文档.v2", ExportFilePrefix.Resolve("  ", "文档.v2.psd", useDocumentName: true));
        Equal("未保存文档", ExportFilePrefix.Resolve("", "未保存文档", useDocumentName: true));
        Equal("", ExportFilePrefix.Resolve("", "文档.psd", useDocumentName: false));
        Equal("", ExportFilePrefix.Resolve("", "", useDocumentName: true));
    });

    Run("Artboard progress shows current and expected count", () =>
    {
        Equal("正在导出... 已生成 3 / 8 个文件", MainViewModel.FormatExportProgressText(3, 8));
        Equal("正在导出... 已生成 3 个文件", MainViewModel.FormatExportProgressText(3, null));
    });

    Run("Artboard merge appends suffix when auto rename is on", () =>
    {
        var sourceDirectory = Path.Combine(tempRoot, "artboard-merge-source-rename");
        var finalDirectory = Path.Combine(tempRoot, "artboard-merge-final-rename");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(finalDirectory);
        var source = Path.Combine(sourceDirectory, "画板.png");
        var existing = Path.Combine(finalDirectory, "画板.png");
        File.WriteAllText(source, "new");
        File.WriteAllText(existing, "old");

        var merged = ExportService.MergeArtboardOutputs(SuccessResult(source), finalDirectory, autoRename: true);
        Equal(Path.Combine(finalDirectory, "画板_1.png"), merged.OutputFiles[0]);
        Equal("old", File.ReadAllText(existing));
        Equal("new", File.ReadAllText(merged.OutputFiles[0]));
    });

    Run("Artboard merge overwrites when auto rename is off", () =>
    {
        var sourceDirectory = Path.Combine(tempRoot, "artboard-merge-source-overwrite");
        var finalDirectory = Path.Combine(tempRoot, "artboard-merge-final-overwrite");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(finalDirectory);
        var source = Path.Combine(sourceDirectory, "画板.jpg");
        var existing = Path.Combine(finalDirectory, "画板.jpg");
        File.WriteAllText(source, "new");
        File.WriteAllText(existing, "old");

        var merged = ExportService.MergeArtboardOutputs(SuccessResult(source), finalDirectory, autoRename: false);
        True(merged.Success, merged.Error?.ToString() ?? "Artboard merge failed without an error.");
        Equal(1, merged.OutputFiles.Count);
        Equal(existing, merged.OutputFiles[0]);
        Equal("new", File.ReadAllText(existing));
    });

    Run("Quick merge applies conflict behavior without custom name", () =>
    {
        var sourceDirectory = Path.Combine(tempRoot, "quick-merge-source");
        var finalDirectory = Path.Combine(tempRoot, "quick-merge-final");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(finalDirectory);
        var source = Path.Combine(sourceDirectory, "Photoshop默认.jpg");
        var existing = Path.Combine(finalDirectory, "Photoshop默认.jpg");
        File.WriteAllText(source, "new");
        File.WriteAllText(existing, "old");

        var merged = ExportService.MergeQuickExportOutputs(
            SuccessResult(source), finalDirectory, string.Empty, autoRename: true);
        Equal(Path.Combine(finalDirectory, "Photoshop默认_1.jpg"), merged.OutputFiles[0]);
        Equal("old", File.ReadAllText(existing));
    });

    Run("Artboard document folder uses sanitized document name", () =>
    {
        var exportPath = Path.Combine(tempRoot, "artboard-document-folder");
        Directory.CreateDirectory(exportPath);
        var settings = new ExportSettings
        {
            ExportPath = exportPath,
            DocumentName = "商品:主图.psb",
            ExportPathOption = ExportPathOption.NewFolder
        };

        var resolved = ExportService.ResolveArtboardExportPath(settings);
        Equal(Path.Combine(exportPath, "商品_主图"), resolved);
        True(Directory.Exists(resolved));
    });

    Run("Artboard document folder appends suffix on conflict", () =>
    {
        var exportPath = Path.Combine(tempRoot, "artboard-folder-conflict");
        var existing = Path.Combine(exportPath, "产品图");
        Directory.CreateDirectory(existing);
        var settings = new ExportSettings
        {
            ExportPath = exportPath,
            DocumentName = "产品图.psd",
            ExportPathOption = ExportPathOption.NewFolder
        };

        var resolved = ExportService.ResolveArtboardExportPath(settings);
        Equal(Path.Combine(exportPath, "产品图_1"), resolved);
        True(Directory.Exists(resolved));
    });

    Run("Artboard overwrite folder reuses document directory", () =>
    {
        var exportPath = Path.Combine(tempRoot, "artboard-folder-overwrite");
        var existing = Path.Combine(exportPath, "产品图");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "keep.txt"), "keep");
        var settings = new ExportSettings
        {
            ExportPath = exportPath,
            DocumentName = "产品图.psd",
            ExportPathOption = ExportPathOption.NewFolderOverwrite
        };

        var resolved = ExportService.ResolveArtboardExportPath(settings);
        Equal(existing, resolved);
        Equal("keep", File.ReadAllText(Path.Combine(existing, "keep.txt")));
    });

    await RunAsync("File watcher detects overwritten output", async () =>
    {
        var exportPath = Path.Combine(tempRoot, "watch");
        Directory.CreateDirectory(exportPath);
        var target = Path.Combine(exportPath, "image.jpg");
        await File.WriteAllTextAsync(target, "old");
        var before = ExportFileWatcher.CaptureSnapshot(exportPath, "jpg");

        _ = Task.Run(async () =>
        {
            await Task.Delay(350);
            await File.WriteAllTextAsync(target, "new-content-that-changes-length");
        });

        var result = await ExportFileWatcher.WaitForChangesAsync(
            exportPath,
            "jpg",
            before,
            timeoutMs: 6_000);

        Equal(1, result.Count);
        True(!result.TimedOut);
        Equal(Path.GetFullPath(target), result.Files[0]);
    });

    await RunAsync("File watcher waits for expected artboard count", async () =>
    {
        var exportPath = Path.Combine(tempRoot, "expected-count");
        Directory.CreateDirectory(exportPath);
        var before = ExportFileWatcher.CaptureSnapshot(exportPath, "png");

        _ = Task.Run(async () =>
        {
            await Task.Delay(300);
            await File.WriteAllTextAsync(Path.Combine(exportPath, "artboard-1.png"), "first");
            await Task.Delay(2_500);
            await File.WriteAllTextAsync(Path.Combine(exportPath, "artboard-2.png"), "second");
        });

        var result = await ExportFileWatcher.WaitForChangesAsync(
            exportPath,
            "png",
            before,
            expectedFileCount: 2,
            timeoutMs: 7_000);

        Equal(2, result.Count);
        True(!result.TimedOut);
    });

    Run("Quick export applies custom file name", () =>
    {
        var dir = Path.Combine(tempRoot, "quick-name");
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "Photoshop默认.jpg");
        File.WriteAllText(source, "new");

        var renamed = ExportService.ApplyQuickExportName(
            SuccessResult(source),
            new ExportSettings { ImageName = "商品主图", AutoRename = true });

        var expected = Path.Combine(dir, "商品主图.jpg");
        True(renamed.Success);
        Equal(expected, renamed.OutputFiles[0]);
        True(File.Exists(expected));
        True(!File.Exists(source));
    });

    Run("Quick export appends suffix on name conflict", () =>
    {
        var dir = Path.Combine(tempRoot, "quick-suffix");
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "Photoshop默认.png");
        var existing = Path.Combine(dir, "商品图.png");
        File.WriteAllText(source, "new");
        File.WriteAllText(existing, "old");

        var renamed = ExportService.ApplyQuickExportName(
            SuccessResult(source),
            new ExportSettings { ImageName = "商品图", AutoRename = true });

        Equal(Path.Combine(dir, "商品图_1.png"), renamed.OutputFiles[0]);
        Equal("old", File.ReadAllText(existing));
        Equal("new", File.ReadAllText(renamed.OutputFiles[0]));
    });

    Run("Quick export overwrites conflict when auto rename is off", () =>
    {
        var dir = Path.Combine(tempRoot, "quick-overwrite");
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "Photoshop默认.jpg");
        var existing = Path.Combine(dir, "商品图.jpg");
        File.WriteAllText(source, "new");
        File.WriteAllText(existing, "old");

        var renamed = ExportService.ApplyQuickExportName(
            SuccessResult(source),
            new ExportSettings { ImageName = "商品图", AutoRename = false });

        Equal(existing, renamed.OutputFiles[0]);
        Equal("new", File.ReadAllText(existing));
        True(!File.Exists(source));
    });

    Run("Quick export keeps Photoshop name when custom name is blank", () =>
    {
        var dir = Path.Combine(tempRoot, "quick-blank");
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "Photoshop默认.jpg");
        File.WriteAllText(source, "new");

        var unchanged = ExportService.ApplyQuickExportName(
            SuccessResult(source),
            new ExportSettings { ImageName = "   ", AutoRename = true });

        Equal(source, unchanged.OutputFiles[0]);
        True(File.Exists(source));
    });

    Run("Quick export keeps names when multiple outputs are detected", () =>
    {
        var dir = Path.Combine(tempRoot, "quick-multiple");
        Directory.CreateDirectory(dir);
        var first = Path.Combine(dir, "画板1.jpg");
        var second = Path.Combine(dir, "画板2.jpg");
        File.WriteAllText(first, "first");
        File.WriteAllText(second, "second");
        var result = new PsBridgeResult
        {
            Success = true,
            ExportedCount = 2,
            OutputFiles = [first, second]
        };

        var unchanged = ExportService.ApplyQuickExportName(
            result,
            new ExportSettings { ImageName = "商品图", AutoRename = true });

        True(unchanged.Success);
        Equal(first, unchanged.OutputFiles[0]);
        Equal(second, unchanged.OutputFiles[1]);
        True(File.Exists(first) && File.Exists(second));
    });

    Run("PNG quick export uses Photoshop quality constant", () =>
    {
        Equal(32, ExportService.ResolveScriptQuality(new ExportSettings
        {
            Format = ExportFormat.Png,
            Quality = 80
        }));
        Equal(80, ExportService.ResolveScriptQuality(new ExportSettings
        {
            Format = ExportFormat.Jpg,
            Quality = 80
        }));

        var template = ScriptLibrary.GetScript("export/quick_export");
        var injected = ScriptParameterInjector.Inject(template, new Dictionary<string, string>
        {
            ["EXPORT_PATH"] = "C:\\\\Exports\\\\",
            ["FORMAT"] = "png",
            ["QUALITY"] = ExportService.ResolveScriptQuality(new ExportSettings
            {
                Format = ExportFormat.Png,
                Quality = 80
            }).ToString(),
            ["RENAME"] = "true",
            ["IMAGE_NAME"] = string.Empty,
            ["WITH_DOC_NAME"] = "false",
            ["SHOW_HINT"] = "false"
        });
        True(injected.Contains("const quality = 32;"));
        Equal(0, ScriptLibrary.ExtractPlaceholders(injected).Count);
    });

    Run("Layer script receives all configured placeholders", () =>
    {
        var template = ScriptLibrary.GetScript(ScriptNames.ExportLayerBatch);
        var placeholders = ScriptLibrary.ExtractPlaceholders(template);
        True(placeholders.Contains("WITH_DOC_NAME"));
        True(!placeholders.Contains("SHOW_HINT"));

        var injected = ScriptParameterInjector.Inject(template, new Dictionary<string, string>
        {
            ["EXPORT_PATH"] = "C:\\\\Exports\\\\",
            ["EXPORT_FOLDER_URL"] = "file:/C:/Exports",
            ["RESULT_FOLDER_URL"] = "file:/C:/Task",
            ["FILE_NAME"] = "export",
            ["SLICE_MODE"] = "false",
            ["RECURSIVE"] = "false",
            ["SELECTED_ONLY"] = "false",
            ["FORMAT"] = "jpg",
            ["QUALITY"] = "80",
            ["RENAME"] = "true",
            ["EXISTING_EXPORT_NAMES"] = "[\"已有.jpg\"]",
            ["IMAGE_NAME"] = "demo",
            ["WITH_DOC_NAME"] = "false",
            ["SHOW_HINT"] = "true"
        });
        Equal(0, ScriptLibrary.ExtractPlaceholders(injected).Count);
        True(injected.Contains("const withDocName = false;"));
        True(!injected.Contains("showAlert"));
        True(injected.Contains("const format = \"jpg\".toLowerCase();"));
        True(injected.Contains("const quality = 80;"));
        True(injected.Contains("const imageName = \"demo\";"));
        True(injected.Contains("new Set([\"已有.jpg\"].map("));
        True(!injected.Contains("folder.getEntries()"));
        True(!ScriptLibrary.GetScript(ScriptNames.ExportSlices).Contains("folder.getEntries()"));
    });

    Run("Layer script exports only top-level layers and restores visibility", () =>
    {
        var template = ScriptLibrary.GetScript(ScriptNames.ExportLayerBatch);
        True(template.Contains("Array.from(doc.layers)"));
        True(!template.Contains("doc.artLayers"));
        True(!template.Contains("function exportLayerRecursive"));
        True(template.Contains("visibility.get(layer.id)"));
        True(template.Contains("finally {"));
        True(template.Contains("layer.visible = visibility.get(layer.id)"));
    });

    await RunAsync("Export failure logs parameters and full exception before Photoshop dispatch", async () =>
    {
        var logPath = Path.Combine(tempRoot, "export-diagnostics.log");
        var logger = new PsExporter.Core.Logging.FileLogger(logPath);
        var service = new ExportService(new UxpScriptBridge(), logger);
        try
        {
            await service.ExportSlicesAsync(new ExportSettings { ExportPath = "bad\0path", FileName = "export" });
            throw new Exception("Invalid output path must fail before dispatch.");
        }
        catch (ArgumentException) { }
        var contents = logger.ReadAll();
        True(contents.Contains("[task="));
        True(contents.Contains("参数="));
        True(contents.Contains("sliceMode=true"));
        True(contents.Contains("调用链异常"));
        True(contents.Contains("ArgumentException"));
        Equal(logPath, logger.LogPath);
    });

    Run("UXP folder URL preserves Chinese names and spaces", () =>
    {
        Equal("file:/C:/导出 文件", ExportService.ToUxpFolderUrl(@"C:\导出 文件"));
        Equal("file://192.168.1.199/共享空间/诚睿", ExportService.ToUxpFolderUrl(@"\\192.168.1.199\共享空间\诚睿"));
    });

    await RunAsync("UXP receipt waits for done even when files already exist", async () =>
    {
        var output = Path.Combine(tempRoot, "uxp-receipt");
        Directory.CreateDirectory(output);
        var receipt = Path.Combine(output, "result.json");
        File.WriteAllText(Path.Combine(output, "one.png"), "image");
        File.WriteAllText(receipt, "{\"done\":false,\"success\":false,\"files\":[\"one.png\"]}");
        var progress = new List<string>();
        var waiting = UxpTaskWatcher.WaitForCompletionAsync(receipt, output,
            (name, _) => progress.Add(name), timeoutMs: 5000);
        await Task.Delay(600);
        True(!waiting.IsCompleted, "A partial progress receipt must not end the task.");
        File.WriteAllText(receipt, "{\"done\":true,\"success\":true,\"files\":[\"one.png\"]}");
        var result = await waiting;
        True(result.Success);
        Equal(1, result.ExportedCount);
        Equal(1, progress.Count);
    });

    await RunAsync("Document action receipt succeeds without image files", async () =>
    {
        var receipt = Path.Combine(tempRoot, "save-and-close.json");
        File.WriteAllText(receipt, """{"done":true,"success":true}""");
        var action = await UxpTaskWatcher.WaitForCompletionAsync(receipt, exportPath: null);
        True(action.Success);
        True(!(await UxpTaskWatcher.WaitForCompletionAsync(receipt, tempRoot)).Success);
        File.WriteAllText(receipt, """{"done":true,"success":false,"error":"save failed"}""");
        var failed = await UxpTaskWatcher.WaitForCompletionAsync(receipt, exportPath: null);
        True(!failed.Success);
        Equal("save failed", failed.Error?.Message);
    });

    await RunAsync("UXP error receipt preserves completed files", async () =>
    {
        var output = Path.Combine(tempRoot, "uxp-error");
        Directory.CreateDirectory(output);
        var receipt = Path.Combine(output, "result.json");
        File.WriteAllText(Path.Combine(output, "partial.jpg"), "image");
        File.WriteAllText(receipt, "{\"done\":true,\"success\":false,\"files\":[\"partial.jpg\"],\"error\":\"save failed\"}");
        var result = await UxpTaskWatcher.WaitForCompletionAsync(receipt, output);
        True(!result.Success);
        Equal(1, result.ExportedCount);
        Equal("save failed", result.Error!.Message);
    });

    await RunAsync("UXP success receipt rejects missing outputs", async () =>
    {
        var receipt = Path.Combine(tempRoot, "missing-result.json");
        File.WriteAllText(receipt, "{\"done\":true,\"success\":true,\"files\":[\"missing.png\"]}");
        var result = await UxpTaskWatcher.WaitForCompletionAsync(receipt, tempRoot);
        True(!result.Success);
        Equal(0, result.ExportedCount);
    });

    await RunAsync("UXP timeout keeps partial output", async () =>
    {
        var output = Path.Combine(tempRoot, "uxp-timeout");
        Directory.CreateDirectory(output);
        var receipt = Path.Combine(output, "result.json");
        var image = Path.Combine(output, "partial.png");
        File.WriteAllText(image, "image");
        File.WriteAllText(receipt, "{\"done\":false,\"files\":[\"partial.png\"],\"stage\":\"保存 PNG 副本\",\"path\":\"test.png\"}");
        var result = await UxpTaskWatcher.WaitForCompletionAsync(receipt, output, timeoutMs: 300);
        True(result.Error is TimeoutException);
        True(result.Error!.Message.Contains("保存 PNG 副本"));
        True(result.Error.Message.Contains("test.png"));
        Equal(1, result.ExportedCount);
        True(File.Exists(image));
        True(File.Exists(receipt));
    });

    await RunAsync("UXP receipt tolerates in-progress JSON and supports cancellation", async () =>
    {
        var receipt = Path.Combine(tempRoot, "incomplete-result.json");
        File.WriteAllText(receipt, "{\"done\":");
        using var cancel = new CancellationTokenSource(350);
        try
        {
            await UxpTaskWatcher.WaitForCompletionAsync(receipt, tempRoot, ct: cancel.Token);
            throw new InvalidOperationException("Expected cancellation.");
        }
        catch (OperationCanceledException) { }
        True(File.Exists(receipt));
    });

    Run("Dock snapshot scale respects DPI and bounds memory", () =>
    {
        Equal(1d, MainWindow.CalculateCaptureScale(725, 515, 1));
        Equal(3d, MainWindow.CalculateCaptureScale(725, 515, 3));
        var scale = MainWindow.CalculateCaptureScale(3840, 2160, 2);
        True(3840 * 2160 * scale * scale <= 8_000_001);
    });

    Run("Docked idle and animation states suspend status rendering", () =>
    {
        True(!MainWindow.ShouldRunStatusAnimation(true, false, false));
        True(!MainWindow.ShouldRunStatusAnimation(true, true, true));
        True(MainWindow.ShouldRunStatusAnimation(true, true, false));
        True(MainWindow.ShouldRunStatusAnimation(false, false, false));
    });

    Run("Genie frames reuse topology without mutating submitted vertices", () =>
    {
        foreach (var edge in new[] { DockEdge.Left, DockEdge.Right, DockEdge.Top, DockEdge.Bottom })
        {
            var first = EdgeSnapHelper.CalculateGenieMeshData(edge, 0, true, 725, 515);
            var point = first.Vertices[100];
            var next = EdgeSnapHelper.CalculateGenieMeshData(edge, 0.6, true, 725, 515, topology: first);
            True(ReferenceEquals(first.TexCoords, next.TexCoords));
            True(ReferenceEquals(first.Indices, next.Indices));
            True(!ReferenceEquals(first.Vertices, next.Vertices));
            Equal(point, first.Vertices[100]);
            var resized = EdgeSnapHelper.CalculateGenieMeshData(edge, 0.6, true, 800, 600, topology: next);
            True(!ReferenceEquals(next.TexCoords, resized.TexCoords));
        }
    });

    Run("All active export scripts avoid Photoshop dialogs", () =>
    {
        foreach (var name in new[] { ScriptNames.ExportQuick, ScriptNames.ExportAllArtboards,
                     ScriptNames.ExportSelectedArtboards, ScriptNames.ExportLayerBatch, "export/receipt_task" })
        {
            var script = ScriptLibrary.GetScript(name);
            True(!script.Contains("showAlert("));
            True(!script.Contains("alert("));
        }
    });

    Run("Second application instance signals the primary instance", () =>
    {
        var instanceName = @"Local\PSexpV3.Tests." + Guid.NewGuid().ToString("N");
        using var primary = new SingleInstanceCoordinator(instanceName);
        using var secondary = new SingleInstanceCoordinator(instanceName);
        True(primary.IsPrimary);
        True(!secondary.IsPrimary);

        using var activated = new ManualResetEventSlim(false);
        primary.StartListening(() => activated.Set());
        secondary.SignalPrimary();
        True(activated.Wait(TimeSpan.FromSeconds(2)), "Primary instance did not receive activation signal.");
    });
    Run("EdgeSnapHelper calculates snap target for left, right, top and bottom edges", () =>
    {
        var work = new Avalonia.PixelRect(0, 0, 1920, 1080);

        // 左贴边
        var snapLeft = EdgeSnapHelper.CalculateSnapTarget(new Avalonia.PixelRect(15, 100, 400, 300), work, thresholdPx: 24);
        True(snapLeft.IsSnapped && snapLeft.SnappedLeft && !snapLeft.SnappedRight);
        Equal(0, snapLeft.TargetPosition.X);
        Equal(100, snapLeft.TargetPosition.Y);

        // 右贴边
        var snapRight = EdgeSnapHelper.CalculateSnapTarget(new Avalonia.PixelRect(1510, 100, 400, 300), work, thresholdPx: 24);
        True(snapRight.IsSnapped && snapRight.SnappedRight && !snapRight.SnappedLeft);
        Equal(1520, snapRight.TargetPosition.X);
        Equal(100, snapRight.TargetPosition.Y);

        // 顶贴边
        var snapTop = EdgeSnapHelper.CalculateSnapTarget(new Avalonia.PixelRect(100, 12, 400, 300), work, thresholdPx: 24);
        True(snapTop.IsSnapped && snapTop.SnappedTop && !snapTop.SnappedBottom);
        Equal(100, snapTop.TargetPosition.X);
        Equal(0, snapTop.TargetPosition.Y);

        // 底贴边
        var snapBottom = EdgeSnapHelper.CalculateSnapTarget(new Avalonia.PixelRect(100, 770, 400, 300), work, thresholdPx: 24);
        True(snapBottom.IsSnapped && snapBottom.SnappedBottom && !snapBottom.SnappedTop);
        Equal(100, snapBottom.TargetPosition.X);
        Equal(780, snapBottom.TargetPosition.Y);
    });

    Run("EdgeSnapHelper calculates hidden dock targets and accepts slight overshoot", () =>
    {
        var work = new Avalonia.PixelRect(0, 0, 1920, 1080);

        var left = EdgeSnapHelper.CalculateDockTarget(
            new Avalonia.PixelRect(-26, 300, 360, 572), work, thresholdPx: 48, peekPx: 12);
        True(left.IsDockable && left.Edge == DockEdge.Left);
        Equal(new Avalonia.PixelPoint(0, 300), left.ExpandedPosition);
        Equal(new Avalonia.PixelPoint(-348, 300), left.HiddenPosition);

        var right = EdgeSnapHelper.CalculateDockTarget(
            new Avalonia.PixelRect(1586, 250, 360, 572), work, thresholdPx: 48, peekPx: 12);
        True(right.IsDockable && right.Edge == DockEdge.Right);
        Equal(new Avalonia.PixelPoint(1560, 250), right.ExpandedPosition);
        Equal(new Avalonia.PixelPoint(1908, 250), right.HiddenPosition);

        var top = EdgeSnapHelper.CalculateDockTarget(
            new Avalonia.PixelRect(800, -18, 360, 572), work, thresholdPx: 48, peekPx: 12);
        True(top.IsDockable && top.Edge == DockEdge.Top);
        Equal(new Avalonia.PixelPoint(800, 0), top.ExpandedPosition);
        Equal(new Avalonia.PixelPoint(800, -560), top.HiddenPosition);

        var bottom = EdgeSnapHelper.CalculateDockTarget(
            new Avalonia.PixelRect(800, 526, 360, 572), work, thresholdPx: 48, peekPx: 12);
        True(bottom.IsDockable && bottom.Edge == DockEdge.Bottom);
        Equal(new Avalonia.PixelPoint(800, 508), bottom.ExpandedPosition);
        Equal(new Avalonia.PixelPoint(800, 1068), bottom.HiddenPosition);
    });

    Run("EdgeSnapHelper docks windows that are half outside but still partially visible", () =>
    {
        var work = new Avalonia.PixelRect(0, 0, 1920, 1080);

        var halfLeft = EdgeSnapHelper.CalculateDockTarget(
            new Avalonia.PixelRect(-200, 300, 400, 300), work, thresholdPx: 48, peekPx: 12);
        True(halfLeft.IsDockable && halfLeft.Edge == DockEdge.Left);

        var halfRight = EdgeSnapHelper.CalculateDockTarget(
            new Avalonia.PixelRect(1720, 300, 400, 300), work, thresholdPx: 48, peekPx: 12);
        True(halfRight.IsDockable && halfRight.Edge == DockEdge.Right);

        var fullyOutside = EdgeSnapHelper.CalculateDockTarget(
            new Avalonia.PixelRect(-450, 300, 400, 300), work, thresholdPx: 48, peekPx: 12);
        True(!fullyOutside.IsDockable);
    });

    Run("EdgeSnapHelper distinguishes outer edges from multi-monitor shared edges", () =>
    {
        var primary = new Avalonia.PixelRect(0, 0, 1920, 1080);
        var secondary = new Avalonia.PixelRect(1920, 0, 1920, 1080);
        var screens = new[] { primary, secondary };
        True(EdgeSnapHelper.IsOuterScreenEdge(primary, screens, DockEdge.Left));
        True(!EdgeSnapHelper.IsOuterScreenEdge(primary, screens, DockEdge.Right));
        True(!EdgeSnapHelper.IsOuterScreenEdge(secondary, screens, DockEdge.Left));
        True(EdgeSnapHelper.IsOuterScreenEdge(secondary, screens, DockEdge.Right));
    });

    Run("EdgeSnapHelper calculates precise hot zones around hidden window strip", () =>
    {
        var work = new Avalonia.PixelRect(0, 0, 1920, 1080);

        var leftZone = EdgeSnapHelper.CalculateDockHotZone(
            new Avalonia.PixelRect(-348, 300, 360, 572), work, DockEdge.Left, 26, 16);
        Equal(new Avalonia.PixelRect(0, 284, 38, 604), leftZone);

        var rightZone = EdgeSnapHelper.CalculateDockHotZone(
            new Avalonia.PixelRect(1908, 250, 360, 572), work, DockEdge.Right, 26, 16);
        Equal(new Avalonia.PixelRect(1882, 234, 38, 604), rightZone);

        var topZone = EdgeSnapHelper.CalculateDockHotZone(
            new Avalonia.PixelRect(800, -560, 360, 572), work, DockEdge.Top, 26, 16);
        Equal(new Avalonia.PixelRect(784, 0, 392, 38), topZone);

        var bottomZone = EdgeSnapHelper.CalculateDockHotZone(
            new Avalonia.PixelRect(800, 1068, 360, 572), work, DockEdge.Bottom, 26, 16);
        Equal(new Avalonia.PixelRect(784, 1042, 392, 38), bottomZone);

        True(EdgeSnapHelper.IsPointInRect(leftZone, 20, 500));
        True(!EdgeSnapHelper.IsPointInRect(leftZone, 100, 500));
    });

    Run("Edge dock keeps temporary topmost independent of user topmost setting", () =>
    {
        True(MainViewModel.CalculateEffectiveTopmost(false, false, true));
        True(!MainViewModel.CalculateEffectiveTopmost(false, false, false));
        True(MainViewModel.CalculateEffectiveTopmost(true, false, false));
        True(MainViewModel.CalculateEffectiveTopmost(false, true, false));
    });

    Run("EdgeSnapHelper chooses nearest edge near a corner", () =>
    {
        var work = new Avalonia.PixelRect(0, 0, 1920, 1080);
        var dock = EdgeSnapHelper.CalculateDockTarget(
            new Avalonia.PixelRect(9, 21, 400, 300), work, thresholdPx: 48, peekPx: 12);
        True(dock.IsDockable && dock.Edge == DockEdge.Left);
    });

    Run("EdgeSnapHelper rejects edge dock outside activation threshold", () =>
    {
        var work = new Avalonia.PixelRect(0, 0, 1920, 1080);
        var dock = EdgeSnapHelper.CalculateDockTarget(
            new Avalonia.PixelRect(100, 100, 400, 300), work, thresholdPx: 48, peekPx: 12);
        True(!dock.IsDockable && dock.Edge == DockEdge.None);
    });

    Run("EdgeSnapHelper calculates corner snap target when two edges snap simultaneously", () =>
    {
        var work = new Avalonia.PixelRect(0, 0, 1920, 1080);
        var corner = EdgeSnapHelper.CalculateSnapTarget(new Avalonia.PixelRect(10, 10, 400, 300), work, thresholdPx: 24);
        True(corner.IsSnapped && corner.SnappedLeft && corner.SnappedTop);
        Equal(0, corner.TargetPosition.X);
        Equal(0, corner.TargetPosition.Y);
    });

    Run("EdgeSnapHelper rejects snap when window is beyond threshold", () =>
    {
        var work = new Avalonia.PixelRect(0, 0, 1920, 1080);
        var noSnap = EdgeSnapHelper.CalculateSnapTarget(new Avalonia.PixelRect(100, 100, 400, 300), work, thresholdPx: 24);
        True(!noSnap.IsSnapped);
        Equal(100, noSnap.TargetPosition.X);
        Equal(100, noSnap.TargetPosition.Y);
    });

    Run("EdgeSnapHelper damped spring provides smooth Q-bounce and settles", () =>
    {
        Equal(0.0, EdgeSnapHelper.CalculateElasticBounceProgress(0.0));
        Equal(1.0, EdgeSnapHelper.CalculateElasticBounceProgress(1.0));

        var firstFrame = EdgeSnapHelper.CalculateElasticBounceProgress(0.05);
        var secondFrame = EdgeSnapHelper.CalculateElasticBounceProgress(0.10);
        True(firstFrame > 0 && secondFrame > firstFrame, "Spring should accelerate smoothly from zero velocity.");

        var peak = Enumerable.Range(1, 99)
            .Select(index => EdgeSnapHelper.CalculateElasticBounceProgress(index / 100.0))
            .Max();
        True(peak > 1.03 && peak < 1.08, $"Expected a controlled 3%-8% overshoot, got {peak}.");
        True(Math.Abs(EdgeSnapHelper.CalculateElasticBounceProgress(0.70) - 1.0) < 0.01);
        True(Math.Abs(EdgeSnapHelper.CalculateElasticBounceProgress(0.90) - 1.0) < 0.001);

        var start = new Avalonia.PixelPoint(100, 100);
        var target = new Avalonia.PixelPoint(0, 100);
        var pos0 = EdgeSnapHelper.CalculateInterpolatedPosition(start, target, 0.0);
        var pos1 = EdgeSnapHelper.CalculateInterpolatedPosition(start, target, 1.0);
        Equal(start, pos0);
        Equal(target, pos1);
    });

    Run("EdgeSnapHelper Genie Mesh Data calculates non-linear funnel vertices and spine wave", () =>
    {
        var meshStart = EdgeSnapHelper.CalculateGenieMeshData(DockEdge.Left, 0.0, isHiding: true, 500f, 400f, cols: 12, rows: 12);
        Equal(13 * 13, meshStart.Vertices.Length);
        Equal(12 * 12 * 6, meshStart.Indices.Length);
        Equal(0f, meshStart.Vertices[0].X);

        var meshMid = EdgeSnapHelper.CalculateGenieMeshData(DockEdge.Left, 0.5, isHiding: true, 500f, 400f, cols: 12, rows: 12);
        True(meshMid.Vertices[0].X < 50f, "Left edge vertices should squeeze near peek strip");
        True(meshMid.Vertices[12].X > 200f, "Tail edge vertices should follow with delay");

        var meshEnd = EdgeSnapHelper.CalculateGenieMeshData(DockEdge.Left, 1.0, isHiding: true, 500f, 400f, cols: 12, rows: 12);
        True(meshEnd.Vertices[12].X < 30f, "All vertices should compress into dock strip at end");

        var revealStart = EdgeSnapHelper.CalculateGenieMeshData(DockEdge.Left, 0.0, isHiding: false, 500f, 400f, cols: 12, rows: 12);
        True(revealStart.Vertices[12].X < 30f, "Reveal must start from the compressed strip");
        var revealEnd = EdgeSnapHelper.CalculateGenieMeshData(DockEdge.Left, 1.0, isHiding: false, 500f, 400f, cols: 12, rows: 12);
        Equal(500f, revealEnd.Vertices[12].X);

        var rightEnd = EdgeSnapHelper.CalculateGenieMeshData(DockEdge.Right, 1.0, isHiding: true, 500f, 400f, cols: 12, rows: 12);
        True(rightEnd.Vertices[0].X > 470f, "Right dock must compress all columns near the right strip");
        var topEnd = EdgeSnapHelper.CalculateGenieMeshData(DockEdge.Top, 1.0, isHiding: true, 500f, 400f, cols: 12, rows: 12);
        True(topEnd.Vertices[12 * 13].Y < 30f, "Top dock must compress all rows near the top strip");
        var bottomEnd = EdgeSnapHelper.CalculateGenieMeshData(DockEdge.Bottom, 1.0, isHiding: true, 500f, 400f, cols: 12, rows: 12);
        True(bottomEnd.Vertices[0].Y > 370f, "Bottom dock must compress all rows near the bottom strip");

        foreach (var index in meshEnd.Indices)
            True(index < meshEnd.Vertices.Length, "Triangle index must stay inside vertex array");
        Equal(meshEnd.Vertices.Length, meshEnd.TexCoords.Length);
    });

    Run("EdgeSnapHelper docks a half-visible focused icon on every screen edge", () =>
    {
        var work = new Avalonia.PixelRect(0, 0, 1920, 1080);
        var expanded = new Avalonia.PixelPoint(400, 300);

        var left = EdgeSnapHelper.CalculateDockIconPlacement(work, expanded, 600, 400, DockEdge.Left, 56, 28);
        Equal(-28, left.Position.X);
        Equal(472, left.Position.Y);
        Equal(56, left.SizePx);

        var right = EdgeSnapHelper.CalculateDockIconPlacement(work, expanded, 600, 400, DockEdge.Right, 56, 28);
        Equal(1892, right.Position.X);
        Equal(472, right.Position.Y);

        var top = EdgeSnapHelper.CalculateDockIconPlacement(work, expanded, 600, 400, DockEdge.Top, 56, 28);
        Equal(672, top.Position.X);
        Equal(-28, top.Position.Y);

        var bottom = EdgeSnapHelper.CalculateDockIconPlacement(work, expanded, 600, 400, DockEdge.Bottom, 56, 28);
        Equal(672, bottom.Position.X);
        Equal(1052, bottom.Position.Y);
    });

    Run("Genie animation progress is monotonic with smooth endpoints", () =>
    {
        Equal(0d, EdgeSnapHelper.CalculateGenieAnimationProgress(0));
        Equal(1d, EdgeSnapHelper.CalculateGenieAnimationProgress(1));
        var previous = 0d;
        foreach (var index in Enumerable.Range(1, 99))
        {
            var current = EdgeSnapHelper.CalculateGenieAnimationProgress(index / 100d);
            True(current > previous, "Genie progress must not overshoot or move backward.");
            previous = current;
        }
    });

    Run("Dock reveal target coordinates do not match offscreen position before animation start", () =>
    {
        var offscreen = new Avalonia.PixelPoint(-32000, -32000);
        var expanded = new Avalonia.PixelPoint(400, 300);
        True(offscreen != expanded, "Reveal animation must not be short-circuited by equal position check.");
    });

    Run("Summon hotkey maps only the four contiguous Avalonia key ranges", () =>
    {
        // Avalonia 12 的 Key 取值不是 Windows 虚拟键码；一旦上游改序号，这里先失败，而不是热键悄悄错位。
        Equal(44, (int)Key.A);
        Equal(34, (int)Key.D0);
        Equal(74, (int)Key.NumPad0);
        Equal(90, (int)Key.F1);
        Equal(1, (int)KeyModifiers.Alt);
        Equal(2, (int)KeyModifiers.Control);
        Equal(4, (int)KeyModifiers.Shift);
        Equal(8, (int)KeyModifiers.Meta);

        Key[] keys = [Key.A, Key.Z, Key.D0, Key.D9, Key.NumPad0, Key.NumPad9, Key.F1, Key.F12];
        uint[] virtualKeys = [0x41, 0x5A, 0x30, 0x39, 0x60, 0x69, 0x70, 0x7B];
        for (var index = 0; index < keys.Length; index++)
        {
            True(SummonHotkey.TryGetVirtualKey(keys[index], out var actual), $"{keys[index]} 应能映射成虚拟键码");
            Equal(virtualKeys[index], actual);
        }
        foreach (var key in new[] { Key.None, Key.Escape, Key.Space, Key.Tab, Key.OemMinus, Key.Add })
            True(!SummonHotkey.TryGetVirtualKey(key, out _), $"{key} 不支持作为唤起热键");
    });

    Run("Summon hotkey text round-trips and rejects combos RegisterHotKey would swallow", () =>
    {
        Equal("Ctrl+Alt+E", SummonHotkey.Normalize("alt+ctrl+e", "Ctrl+Alt+Shift+Z"));
        Equal("Ctrl+Alt+Shift+Win+F12", SummonHotkey.Normalize("ctrl + alt + SHIFT + win + f12", "Ctrl+Alt+E"));
        Equal("Ctrl+NumPad7", SummonHotkey.Normalize("Ctrl+NumPad7", "Ctrl+Alt+E"));
        True(SummonHotkey.TryFormat(Key.E, KeyModifiers.Control | KeyModifiers.Alt, out var formatted));
        Equal("Ctrl+Alt+E", formatted);

        string[] dirty = ["E", "5", "NumPad3", "Ctrl+Alt", "Ctrl+Escape", "Ctrl+OemMinus", "Ctrl+E+R", "Ctrl+", "NumPad", "Alt+Shift+Win"];
        True(!SummonHotkey.TryParse("", out _, out _), "“” 应被拒绝");
        True(!SummonHotkey.TryParse("   ", out _, out _), "“   ” 应被拒绝");
        foreach (var text in dirty)
            True(!SummonHotkey.TryParse(text, out _, out _), $"“{text}” 应被拒绝");
        // 空串是合法的「关闭」，原样保留；只有读不懂的脏值才回落默认组合。
        Equal("", SummonHotkey.Normalize("", "Ctrl+Alt+E"));
        Equal("", SummonHotkey.Normalize("   ", "Ctrl+Alt+E"));
        foreach (var text in dirty)
            Equal("Ctrl+Alt+E", SummonHotkey.Normalize(text, "Ctrl+Alt+E"));
        Equal("Ctrl+Alt+E", SummonHotkey.Normalize("召唤神兽", "Ctrl+Alt+E"));
    });

    Run("Summon hotkey allows bare function keys and refuses bare letters", () =>
    {
        // 单用功能键是用户要的口子，但只开 F1–F12：裸字母/数字会吞掉系统里的普通输入。
        foreach (var key in new[] { Key.F1, Key.F5, Key.F6, Key.F12 })
        {
            True(SummonHotkey.TryParse(key.ToString(), out var parsed, out var modifiers) && modifiers == KeyModifiers.None,
                $"{key} 应能单独作为唤起键");
            Equal(key, parsed);
            True(SummonHotkey.TryFormat(parsed, modifiers, out var text));
            Equal(key.ToString(), text);
        }
        foreach (var key in new[] { Key.A, Key.D5, Key.NumPad3, Key.F13 })
            True(!SummonHotkey.Supports(key, KeyModifiers.None), $"{key} 不应允许单键注册");
        Equal(0u, SummonHotkey.NativeModifiers(KeyModifiers.None));
        Equal(0x3u, SummonHotkey.NativeModifiers(KeyModifiers.Alt | KeyModifiers.Control));
        Equal(0xCu, SummonHotkey.NativeModifiers(KeyModifiers.Shift | KeyModifiers.Meta));
        Equal(0xFu, SummonHotkey.NativeModifiers(KeyModifiers.Alt | KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Meta));
    });

    Run("Summon hotkey matches the bound executable and supports an empty global binding", () =>
    {
        var program = Path.Combine(tempRoot, "Example.exe");
        True(SummonHotkey.MatchesProgram("", null));
        True(SummonHotkey.MatchesProgram(AppSettings.DefaultSummonHotkeyProgram, Path.Combine(tempRoot, "PHOTOSHOP.EXE")));
        True(!SummonHotkey.MatchesProgram(AppSettings.DefaultSummonHotkeyProgram, Path.Combine(tempRoot, "Photoshop Helper.exe")));
        True(SummonHotkey.MatchesProgram(program, program.ToUpperInvariant()));
        True(!SummonHotkey.MatchesProgram(program, Path.Combine(tempRoot, "other", "Example.exe")));
        True(!SummonHotkey.MatchesProgram(program, null));
    });

    Run("Summon placement supports beside and centered positions within the working area", () =>
    {
        var area = new PixelRect(0, 0, 1920, 1080);
        // 默认落在光标右下，留一个空隙。
        Equal(new PixelPoint(116, 116), SummonPlacement.Beside(new PixelPoint(100, 100), 600, 800, area, 16));
        // 右边放不下就翻到光标左侧，且整块仍在工作区内。
        Equal(new PixelPoint(1084, 116), SummonPlacement.Beside(new PixelPoint(1700, 100), 600, 800, area, 16));
        // 下边放不下就翻到光标上方。
        Equal(new PixelPoint(116, 84), SummonPlacement.Beside(new PixelPoint(100, 900), 600, 800, area, 16));
        // 角落：两条边同时翻转。
        Equal(new PixelPoint(1284, 244), SummonPlacement.Beside(new PixelPoint(1900, 1060), 600, 800, area, 16));
        // 窗口比工作区还高：翻到哪都放不下，退化成贴顶，绝不越界。
        Equal(new PixelPoint(116, 0), SummonPlacement.Beside(new PixelPoint(100, 100), 600, 1200, area, 16));
        // 副屏带偏移的工作区：坐标跟着那块屏，不能被拉回主屏。
        var second = new PixelRect(1920, 0, 1920, 1080);
        Equal(new PixelPoint(2516, 516), SummonPlacement.Beside(new PixelPoint(2500, 500), 600, 400, second, 16));
        Equal(new PixelPoint(2200, 300), SummonPlacement.Centered(new PixelPoint(2500, 500), 600, 400, second));
        Equal(new PixelPoint(0, 0), SummonPlacement.Centered(new PixelPoint(10, 10), 600, 800, area));
        Equal(new PixelPoint(1320, 280), SummonPlacement.Centered(new PixelPoint(1919, 1079), 600, 800, area));
        Equal(new PixelPoint(-1920, 80), SummonPlacement.Centered(new PixelPoint(-1900, 100), 600, 1200,
            new PixelRect(-1920, 80, 1920, 1000)));

        // 逐点核对：只要窗口放得下，就必须整块落在工作区里。
        foreach (var cursor in new[]
        {
            new PixelPoint(0, 0), new PixelPoint(1919, 0), new PixelPoint(0, 1079), new PixelPoint(1919, 1079),
            new PixelPoint(960, 540), new PixelPoint(2, 1077)
        })
        {
            var position = SummonPlacement.Beside(cursor, 600, 800, area, 16);
            var rect = new PixelRect(position, new PixelSize(600, 800));
            True(area.Contains(rect.TopLeft) && area.Contains(new PixelPoint(rect.Right - 1, rect.Bottom - 1)),
                $"{cursor} 唤起的窗口越界：{rect}");
        }
    });

    Run("Script focus reclaim only answers Photoshop steals during a script", () =>
    {
        var ours = new IntPtr(0x1000);
        var photo = new IntPtr(0x2000);
        const uint tick = 123456;
        ScriptFocusGuard.Decision Judge(IntPtr foreground, uint currentTick, bool hasOwner,
            bool isPhotoshop, bool ourWindowIsTool = false) =>
            ScriptFocusGuard.Decide(ours, foreground, tick, currentTick, hasOwner, isPhotoshop, ourWindowIsTool);

        // 前台还归自己：什么都不做。
        Equal(ScriptFocusGuard.Decision.StillOurs, Judge(ours, tick, false, false));
        // 用户点了别的地方（输入时钟走了）：绝不抢。
        Equal(ScriptFocusGuard.Decision.UserInput, Judge(photo, tick + 1, false, true));
        // 顺序：用户动手排在其他判断之前，即使前台是 Photoshop 的对话框也不抢。
        Equal(ScriptFocusGuard.Decision.UserInput, Judge(photo, tick + 1, true, true));
        // Photoshop 弹出的从属窗口（对话框）：前台留给它，哪怕用户没动手。
        Equal(ScriptFocusGuard.Decision.OwnedDialog, Judge(photo, tick, true, true));
        // 抢前台的不是 Photoshop：不插手别人的程序。
        Equal(ScriptFocusGuard.Decision.NotPhotoshop, Judge(new IntPtr(0x3000), tick, false, false));
        // 自己此刻是工具窗口（收纳态）：不抢。
        Equal(ScriptFocusGuard.Decision.ToolWindow, Judge(photo, tick, false, true, ourWindowIsTool: true));
        // 只有"用户没动手、抢前台的是 Photoshop 本体、自己不是工具窗口"这一种组合才拿回来。
        Equal(ScriptFocusGuard.Decision.Reclaim, Judge(photo, tick, false, true));
        // 没有快照（脚本开始时前台本来就不在 PSexp）/当前没有前台：都不动作。
        Equal(ScriptFocusGuard.Decision.NoSnapshot, ScriptFocusGuard.Decide(IntPtr.Zero, photo, tick, tick, false, true, false));
        Equal(ScriptFocusGuard.Decision.NoForeground, ScriptFocusGuard.Decide(ours, IntPtr.Zero, tick, tick, false, true, false));
    });

    Run("Export notice card words its own progress and ending", () =>
    {
        // 有总数才写"已出 / 总数"；总数缺失或为 0 一律退回"几个"，不出现"3 / 0"这种读数。
        Equal("3 / 12", ExportNoticeWindow.ProgressReadout(3, 12));
        Equal("12 / 12", ExportNoticeWindow.ProgressReadout(12, 12));
        Equal("7 个", ExportNoticeWindow.ProgressReadout(7, null));
        Equal("0 个", ExportNoticeWindow.ProgressReadout(0, 0));
        Equal("导出未完成", ExportNoticeWindow.EndedTitle(false));
        Equal("已停止", ExportNoticeWindow.EndedTitle(true));
    });
}
finally
{
    if (failures.Count == 0 && Directory.Exists(tempRoot))
        Directory.Delete(tempRoot, recursive: true);
}

if (failures.Count == 0)
{
    Console.WriteLine($"All PSexp v{UpdateService.CurrentVersion} self-tests passed.");
    // Avalonia/原生依赖偶尔会在特定目录环境残留前台线程；发布前自测完成后必须可靠退出。
    Environment.Exit(0);
}

Console.Error.WriteLine($"Failure samples retained at: {tempRoot}");
try
{
    var testLog = Path.Combine(AppContext.BaseDirectory, "self-test.log");
    File.WriteAllText(testLog, string.Join(Environment.NewLine, failures) + Environment.NewLine + "Samples: " + tempRoot);
    Console.Error.WriteLine($"Full self-test log: {testLog}");
}
catch (Exception logError) { Console.Error.WriteLine("Could not write self-test log: " + logError.Message); }
Console.Error.WriteLine($"{failures.Count} self-test(s) failed:");
foreach (var failure in failures)
    Console.Error.WriteLine("- " + failure);
Environment.Exit(1);

static PsBridgeResult SuccessResult(string path) => new()
{
    Success = true,
    ExportedCount = 1,
    OutputFiles = [path]
};

static ExportInvocation SampleInvocation(ExportInvocationKind kind) => kind switch
{
    ExportInvocationKind.CurrentInterface => new(),
    ExportInvocationKind.PresetById => ExportInvocation.ForPresetId("3f7c2a91b8f4a6"),
    ExportInvocationKind.PresetByName => ExportInvocation.ForPresetName(ExportPresetCategory.Artboards, "秋天的画板"),
    _ => throw new InvalidDataException("指令清单里有形态没有样例：" + kind)
};

static string[] SplitWindowsCommandLine(string command)
{
    var argv = CommandLineToArgvW(command, out var count);
    if (argv == IntPtr.Zero) throw new InvalidOperationException("CommandLineToArgvW failed.");
    try
    {
        var tokens = new string[count];
        for (var index = 0; index < count; index++)
            tokens[index] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size))!;
        return tokens;
    }
    finally { _ = LocalFree(argv); }
}

[DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
static extern IntPtr CommandLineToArgvW(string command, out int count);

[DllImport("kernel32.dll", ExactSpelling = true)]
static extern IntPtr LocalFree(IntPtr memory);

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{name}: {ex}");
    }
}

async Task RunAsync(string name, Func<Task> test)
{
    try
    {
        await test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{name}: {ex}");
    }
}

static void True(bool condition, string? message = null)
{
    if (!condition) throw new InvalidOperationException(message ?? "Expected true.");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
}
