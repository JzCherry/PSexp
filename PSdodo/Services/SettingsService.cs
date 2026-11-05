using System;
using System.IO;
using System.Text.Json;
using PsExporter.Models;

namespace PsExporter.Services;

/// <summary>把 AppSettings 以 JSON 持久化到 %AppData%/PsExporter/settings.json</summary>
public sealed class SettingsService : ISettingsService
{
    private readonly string _directory;
    private readonly string _filePath;

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AppSettings Current { get; private set; } = new();

    public string DataDirectory => _directory;

    public SettingsService()
        : this(Path.Combine(AppProfile.SettingsDirectory, "settings.json"))
    {
    }

    public SettingsService(string filePath)
    {
        _filePath = Path.GetFullPath(filePath);
        _directory = Path.GetDirectoryName(_filePath)
            ?? throw new ArgumentException("设置文件必须包含有效目录", nameof(filePath));
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                Current = JsonSerializer.Deserialize<AppSettings>(json, _opts) ?? CreateDefaults();
            }
            else
            {
                Current = CreateDefaults();
            }
        }
        catch
        {
            Current = CreateDefaults();
        }
        Current.DefaultQuality = Math.Clamp(
            Current.DefaultQuality,
            (int)ExportSettings.MinimumQuality,
            (int)ExportSettings.MaximumQuality);
        (Current.WindowWidth, Current.WindowHeight) = NormalizeWindowSize(
            Current.WindowWidth,
            Current.WindowHeight);
        return Current;
    }

    public static double NormalizeWindowDimension(double value, double defaultValue, double minimumValue)
    {
        if (!double.IsFinite(value) || value <= 0)
            return defaultValue;
        return Math.Max(value, minimumValue);
    }

    public static (double Width, double Height) NormalizeWindowSize(double width, double height)
        => (
            NormalizeWindowDimension(
                width,
                AppSettings.DefaultWindowWidth,
                AppSettings.MinimumWindowWidth),
            NormalizeWindowDimension(
                height,
                AppSettings.DefaultWindowHeight,
                AppSettings.MinimumWindowHeight));

    public bool Save(AppSettings settings)
    {
        var tempPath = _filePath + ".tmp";
        try
        {
            Directory.CreateDirectory(_directory);
            settings.ExtensionData ??= Current.ExtensionData;
            File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, _opts));
            File.Move(tempPath, _filePath, overwrite: true);
            Current = settings;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch
            {
                // 临时文件清理失败不覆盖真实保存结果。
            }
        }
    }

    private static AppSettings CreateDefaults() => new()
    {
        DefaultDestination = ExportDestination.DocumentFolder,
        DefaultExportPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        // A fresh test profile leaves the production shortcut available while both instances run.
        SummonHotkey = AppProfile.IsTestBuild ? "" : AppSettings.DefaultSummonHotkey
    };
}
