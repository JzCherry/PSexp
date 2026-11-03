using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PsExporter;
using PsExporter.Core.Logging;
using PsExporter.Core.Process;
using PsExporter.Core.PsConnection;
using PsExporter.Models;
using PsExporter.Services;
using PsExporter.ViewModels;
using PsExporter.Views;
using System.Text.Json.Nodes;

internal static class PresetTests
{
    private static int _checks;
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); _checks++; Console.WriteLine("PASS " + message); }
    [STAThread]
    private static int Main(string[] args)
    {
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
        var root = Path.Combine(Path.GetTempPath(), "PSexp-preset-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settingsPath = Path.Combine(root, "settings.json");
        var settings = new SettingsService(settingsPath);
        settings.Save(new AppSettings { DefaultQuality = 80, DefaultDestination = ExportDestination.DocumentFolder,
            AutoRefreshOnActivated = false, AutoCheckForUpdates = false, AutoDownloadUpdates = false });
        var photo = new RejectPhotoshop();
        var exporter = new RejectExporter();
        MainViewModel Make() => new(photo, exporter, new SettingsService(settingsPath), new FileLogger(Path.Combine(root, "test.log")));
        var vm = Make();
        void Save(string name)
        {
            vm.SaveNewPresetCommand.Execute(null); vm.PresetEditorName = name; vm.ConfirmPresetEditorCommand.Execute(null);
            Check(!vm.IsPresetEditorOpen && vm.PresetDisplayName == name, "save " + name);
        }
        try
        {
            Check(vm.GetExportCommandWindowSize() == (360d, 480d), "command window opens at the compact size by default");
            var commandSettings = new SettingsService(Path.Combine(root, "command-window", "settings.json"));
            async Task CheckCommandWindowSize(AppSettings saved, (double Width, double Height) expected, string message)
            {
                commandSettings.Save(saved);
                var model = new MainViewModel(photo, exporter, commandSettings, new FileLogger(Path.Combine(root, "test.log")));
                try { Check(model.GetExportCommandWindowSize() == expected, message); }
                finally { await model.StopDocumentEventsAsync(); }
            }
            CheckCommandWindowSize(new() { ExportCommandWindowWidth = 440, ExportCommandWindowHeight = 600 },
                (360, 480), "command window migrates the legacy saved default").GetAwaiter().GetResult();
            CheckCommandWindowSize(new() { ExportCommandWindowWidth = 440, ExportCommandWindowHeight = 600, ExportCommandWindowSizeVersion = 1 },
                (440, 600), "command window retains a size chosen after migration").GetAwaiter().GetResult();
            Check(vm.AvailablePresets.Count == 1 && !vm.AvailablePresets[0].IsSelected, "editable initial default in first workspace");
            vm.Quality = 91; vm.ImageName = "hero"; Save("电商");
            Check(!vm.IsPresetModified, "saved preset matches current parameters");
            vm.Quality = 88;
            Check(vm.IsPresetModified, "temporary change is marked modified");
            vm.SelectedMode = ExportMode.Layers;
            Check(vm.Quality == 80 && vm.AvailablePresets.Count == 1, "layers do not inherit quick-export changes");
            vm.Format = ExportFormat.Png; vm.LayerExportRecursive = true; vm.LayerExportSelectedOnly = false; vm.ImageName = "layer";
            Save("电商");
            Check(vm.Format == ExportFormat.Png && !vm.IsPresetModified, "same preset name is allowed in another workspace");
            vm.SelectedMode = ExportMode.QuickExport;
            Check(vm.Quality == 88 && vm.ImageName == "hero" && vm.IsPresetModified, "return restores unsaved quick-export changes");
            vm.RestorePresetCommand.Execute(null);
            Check(vm.Quality == 91 && !vm.IsPresetModified, "restore uses saved values without exporting");
            vm.SelectedMode = ExportMode.Slices; vm.FileName = ""; vm.SliceFolderName = "slices"; vm.Quality = 82; Save("切片包");
            vm.SelectedMode = ExportMode.AllArtboards; vm.SelectedMode = ExportMode.SelectedArtboards; vm.Quality = 95; Save("选中画板");
            vm.SelectedMode = ExportMode.QuickExport; vm.SelectedMode = ExportMode.AllArtboards;
            Check(vm.SelectedMode == ExportMode.SelectedArtboards && vm.Quality == 95, "all and selected artboards share one workspace and remember scope");
            vm.SelectedMode = ExportMode.Layers;
            Check(vm.Format == ExportFormat.Png && vm.LayerExportRecursive && !vm.LayerExportSelectedOnly && vm.ImageName == "layer", "layer scope and naming restore together");
            vm.SelectedMode = ExportMode.QuickExport; vm.Quality = 79; vm.FlushParameterMemory();
            var resumed = Make();
            Check(resumed.SelectedMode == ExportMode.QuickExport && resumed.Quality == 79 && resumed.PresetDisplayName == "电商" && resumed.IsPresetModified, "restart restores selected preset and unsaved edits");
            resumed.SelectedMode = ExportMode.Slices;
            Check(resumed.SliceFolderName == "slices" && resumed.FileName == "" && resumed.PresetDisplayName == "切片包", "restart restores another workspace including empty prefix");
            resumed.StopDocumentEventsAsync().GetAwaiter().GetResult();
            vm.DeletePresetCommand.Execute(null); vm.ConfirmPresetEditorCommand.Execute(null);
            Check(vm.AvailablePresets.Count == 1 && vm.Quality == 79 && vm.ImageName == "hero", "delete preserves current export parameters");
            vm.ApplyPresetCommand.Execute(vm.AvailablePresets[0]);
            Check(vm.Quality == 80 && vm.RenamePresetCommand.CanExecute(null) && vm.DeletePresetCommand.CanExecute(null), "initial default can be renamed and deleted");
            vm.Quality = 83; vm.SaveCurrentPresetCommand.Execute(null);
            Check(!vm.IsPresetModified && vm.Quality == 83, "initial default can be updated");
            vm.RenamePresetCommand.Execute(null); vm.PresetEditorName = "我的默认"; vm.ConfirmPresetEditorCommand.Execute(null);
            Check(vm.PresetDisplayName == "我的默认", "initial default can be renamed");
            vm.DeletePresetCommand.Execute(null); vm.ConfirmPresetEditorCommand.Execute(null);
            var afterDelete = Make();
            Check(afterDelete.AvailablePresets.Count == 0, "deleted default does not reappear after restart");
            afterDelete.StopDocumentEventsAsync().GetAwaiter().GetResult();
            vm.ExportPath = "C:\\private-output"; vm.Destination = ExportDestination.Custom; vm.ImageName = "private-name";
            vm.SaveNewPresetCommand.Execute(null); vm.PresetEditorName = "通用"; vm.ConfirmPresetEditorCommand.Execute(null);
            var json = vm.ExportCurrentPresets();
            Check(json.Contains("private-output") && json.Contains("private-name"), "new presets always save destination and naming");
            vm.Destination = ExportDestination.Desktop; vm.ImageName = "keep-this";
            Check(vm.IsPresetModified, "destination and naming participate in modified state");
            vm.ApplyPresetCommand.Execute(vm.AvailablePresets.First(item => item.Name == "通用"));
            Check(!vm.IsPresetEditorOpen && vm.Destination == ExportDestination.Custom && vm.ImageName == "private-name", "switch immediately restores complete options without a confirmation editor");
            vm.ImportPresets(json);
            Check(vm.AvailablePresets.Count == 2 && vm.AvailablePresets.Any(item => item.Name == "通用 (2)"), "import merges duplicate names without replacing existing presets");
            var count = vm.AvailablePresets.Count;
            var invalid = JsonNode.Parse(json)!; invalid["version"] = 2; vm.ImportPresets(invalid.ToJsonString());
            Check(vm.AvailablePresets.Count == count, "unknown file version leaves catalog intact");
            invalid["version"] = "1"; vm.ImportPresets(invalid.ToJsonString());
            Check(vm.AvailablePresets.Count == count, "non-numeric file version is rejected without throwing");
            invalid = JsonNode.Parse(json)!; invalid["presets"]![0]!["parameters"]!["mode"] = 999; vm.ImportPresets(invalid.ToJsonString());
            Check(vm.AvailablePresets.Count == count, "invalid scope leaves catalog intact");
            vm.SaveNewPresetCommand.Execute(null); vm.PresetEditorName = "通用"; vm.ConfirmPresetEditorCommand.Execute(null);
            Check(vm.IsPresetEditorOpen && vm.HasPresetEditorError, "duplicate name shows an inline error");
            vm.CancelPresetEditorCommand.Execute(null);
            settings.Save(new AppSettings { AutoRefreshOnActivated = false }); // emulate old code rewriting only settings.json
            var rollback = Make();
            Check(rollback.AvailablePresets.Any(item => item.Name == "通用"), "legacy settings writes do not erase separate preset storage");
            rollback.StopDocumentEventsAsync().GetAwaiter().GetResult();
            Check(photo.Calls == 0 && exporter.Calls == 0, "all preset operations make zero Photoshop or export calls");
            if (args.Length > 0)
            {
                Directory.CreateDirectory(args[0]);
                foreach (var dark in new[] { false, true })
                {
                    App.ApplyTheme(dark ? AppThemeMode.Dark : AppThemeMode.Light);
                    vm.CancelPresetEditorCommand.Execute(null); vm.PresetStatusMessage = "";
                    Render(vm, 284, Path.Combine(args[0], dark ? "compact-dark.png" : "compact-light.png"));
                    vm.SaveNewPresetCommand.Execute(null);
                    Render(vm, 284, Path.Combine(args[0], dark ? "editor-dark.png" : "editor-light.png"));
                    Render(vm, 420, Path.Combine(args[0], dark ? "wide-dark.png" : "wide-light.png"));
                }
            }
            Console.WriteLine($"{_checks} preset integration checks passed; settings={root}");
            return 0;
        }
        finally { vm.StopDocumentEventsAsync().GetAwaiter().GetResult(); }
    }
    private static void Render(MainViewModel vm, double width, string path)
    {
        var bar = new ExportPresetBar { DataContext = vm, Width = width };
        var host = new Window { Content = bar, Width = width, ShowActivated = false, ShowInTaskbar = false };
        bar.Measure(new Size(width, double.PositiveInfinity));
        var height = Math.Ceiling(bar.DesiredSize.Height);
        host.Measure(new Size(width, height)); host.Arrange(new Rect(0, 0, width, height));
        bar.Arrange(new Rect(0, 0, width, height)); bar.UpdateLayout();
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(width * 1.5), (int)Math.Ceiling(height * 1.5)), new Vector(96, 96));
        using (var drawing = bitmap.CreateDrawingContext())
        {
            drawing.DrawRectangle(new VisualBrush(bar) { Stretch = Stretch.Fill,
                SourceRect = new RelativeRect(new Rect(0, 0, width, height), RelativeUnit.Absolute) }, null,
                new Rect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height));
        }
        bitmap.Save(path); host.Close();
        Console.WriteLine("RENDER " + path + $" {width}x{height} DIP");
    }
}

public sealed class RejectPhotoshop : IPhotoshopService
{
    public int Calls;
    public bool IsBridgeAvailable => true;
    public PhotoshopProcessInfo DetectProcess() { Calls++; throw new Exception("Unexpected Photoshop call"); }
    public Task<bool> TryLaunchAsync(string? path, CancellationToken ct = default) => throw new Exception("Unexpected launch");
    public Task<PsDocumentInfo> GetDocumentInfoAsync(CancellationToken ct = default) => throw new Exception("Unexpected document read");
    public Task<(PsDocumentInfo Doc, PsBridgeResult Result)> ProbeDocumentInfoAsync(CancellationToken ct = default) => throw new Exception("Unexpected probe");
    public Task<List<ArtboardInfo>> ListArtboardsAsync(CancellationToken ct = default) => throw new Exception("Unexpected artboards");
    public Task<PsBridgeResult> ExecuteScriptAsync(string script, IReadOnlyDictionary<string, string> args, CancellationToken ct = default) { Calls++; throw new Exception("Unexpected script"); }
}
public sealed class RejectExporter : IExportService
{
    public int Calls;
    private Task<PsBridgeResult> Reject() { Calls++; throw new Exception("Unexpected export"); }
    public Task<PsBridgeResult> QuickExportAsync(ExportSettings s, ExportProgressHandler? p = null, CancellationToken ct = default) => Reject();
    public Task<PsBridgeResult> ExportAllArtboardsAsync(ExportSettings s, ExportProgressHandler? p = null, CancellationToken ct = default) => Reject();
    public Task<PsBridgeResult> ExportSelectedArtboardsAsync(ExportSettings s, ExportProgressHandler? p = null, CancellationToken ct = default) => Reject();
    public Task<PsBridgeResult> ExportLayersAsync(ExportSettings s, ExportProgressHandler? p = null, CancellationToken ct = default) => Reject();
    public Task<PsBridgeResult> ExportSlicesAsync(ExportSettings s, ExportProgressHandler? p = null, CancellationToken ct = default) => Reject();
}
