using System.Text;
using System.Text.Json;
using PsExporter.Models;

namespace PsExporter.Services;

/// <summary>Separate from settings.json so reverting the app does not erase the preset catalog.</summary>
public sealed class ExportPresetsStorage
{
    private readonly string _path;
    private bool _backupBeforeWrite;
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public string Error { get; private set; } = "";
    public bool NeedsPrefixInputMigration { get; private set; }
    public bool NeedsLayerScopeMigration { get; private set; }
    public ExportPresetsStorage(string directory) => _path = Path.Combine(directory, "export-presets.json");

    public ExportPresetsState Load()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            var state = JsonSerializer.Deserialize<ExportPresetsState>(File.ReadAllText(_path), Options)
                ?? throw new InvalidDataException("预设数据为空。");
            if (state.Version != 1) throw new InvalidDataException("预设数据版本不受支持。");
            NeedsPrefixInputMigration = (state.Items ?? []).OfType<ExportPreset>()
                .Any(item => item.Category != ExportPresetCategory.Quick && item.Parameters?.FilePrefix is null)
                || (state.Workspaces ?? []).OfType<ExportPresetWorkspace>()
                .Any(item => item.Category != ExportPresetCategory.Quick && item.Parameters?.FilePrefix is null);
            NeedsLayerScopeMigration = (state.Items ?? []).OfType<ExportPreset>()
                .Any(item => item.Category == ExportPresetCategory.Layers && item.Parameters?.LayerScopeSelectedOnly is null)
                || (state.Workspaces ?? []).OfType<ExportPresetWorkspace>()
                .Any(item => item.Category == ExportPresetCategory.Layers && item.Parameters?.LayerScopeSelectedOnly is null);
            if (NeedsPrefixInputMigration || NeedsLayerScopeMigration) _backupBeforeWrite = true;
            return ExportPresetCatalog.NormalizeState(state);
        }
        catch (Exception error)
        {
            _backupBeforeWrite = true;
            Error = "预设读取失败，原文件会在下次保存前备份：" + error.Message;
            return new();
        }
    }

    public void PreserveOriginalOnNextSave() => _backupBeforeWrite = true;

    public bool Save(ExportPresetsState state)
    {
        var temporary = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (_backupBeforeWrite && File.Exists(_path))
            {
                File.Copy(_path, _path + ".backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), overwrite: false);
                _backupBeforeWrite = false;
            }
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, Options), new UTF8Encoding(false));
            File.Move(temporary, _path, overwrite: true);
            Error = "";
            return true;
        }
        catch (Exception error) { Error = "预设保存失败：" + error.Message; return false; }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
