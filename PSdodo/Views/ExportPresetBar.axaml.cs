using System.ComponentModel;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PsExporter.Services;
using PsExporter.ViewModels;

namespace PsExporter.Views;

public partial class ExportPresetBar : UserControl
{
    private MainViewModel? _model;
    private static readonly FilePickerFileType PresetFileType = new("PSexp 预设") { Patterns = ["*.json"] };
    public ExportPresetBar()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_model is not null) _model.PropertyChanged -= OnModelChanged;
            _model = DataContext as MainViewModel;
            if (_model is not null) _model.PropertyChanged += OnModelChanged;
        };
    }
    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(MainViewModel.IsPresetEditorOpen) || _model?.IsPresetEditorOpen != true) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_model?.IsPresetEditorOpen == true && _model.IsPresetNameEditor && IsEffectivelyVisible
                && TopLevel.GetTopLevel(this) is Window { IsActive: true })
            { PresetNameInput.Focus(); PresetNameInput.SelectAll(); }
        }, DispatcherPriority.Loaded);
    }
    private void OnPresetChoiceClick(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: ExportPresetListItem choice } && _model is { } model)
            model.ApplyPresetCommand.Execute(choice);
        PresetPicker.Flyout?.Hide();
    }
    private void OnMenuActionClick(object? sender, RoutedEventArgs args)
        => Dispatcher.UIThread.Post(() => PresetMore.Flyout?.Hide()); // Let the button command finish before detaching popup content.
    private async void OnImportClick(object? sender, RoutedEventArgs args)
    {
        PresetMore.Flyout?.Hide();
        if (_model is not { CanManagePresets: true } model || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        try
        {
            var files = await storage.OpenFilePickerAsync(new() { Title = "导入 PSexp 预设", AllowMultiple = false, FileTypeFilter = [PresetFileType] });
            if (files.Count == 0 || !model.CanManagePresets) return;
            using var stream = await files[0].OpenReadAsync();
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer)) > 0)
            {
                if (bytes.Length + count > ExportPresetCatalog.MaximumImportBytes) throw new InvalidDataException("预设文件超过 1 MB。");
                bytes.Write(buffer, 0, count);
            }
            model.ImportPresets(Encoding.UTF8.GetString(bytes.ToArray()).TrimStart('\uFEFF'));
        }
        catch (Exception error) { model.ReportPresetError("导入失败：" + error.Message); }
    }
    private async void OnExportClick(object? sender, RoutedEventArgs args)
    {
        PresetMore.Flyout?.Hide();
        if (_model is not { CanManagePresets: true, HasCustomPresets: true } model || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        try
        {
            // Capture the category before the asynchronous file picker can allow another module to be selected.
            var json = model.ExportCurrentPresets();
            var file = await storage.SaveFilePickerAsync(new() { Title = "导出本板块预设", SuggestedFileName = "PSexp-" + model.CurrentModeTitle + "-预设.json", DefaultExtension = "json", FileTypeChoices = [PresetFileType] });
            if (file is null) return;
            using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek) stream.SetLength(0);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync(json);
            model.ReportPresetError("预设已导出。");
        }
        catch (Exception error) { model.ReportPresetError("导出预设失败：" + error.Message); }
    }
}
