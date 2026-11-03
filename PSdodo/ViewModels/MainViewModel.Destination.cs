using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PsExporter.Models;

namespace PsExporter.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] private ExportDestination _destination = ExportDestination.DocumentFolder;
    [ObservableProperty] private bool _exportTempDocumentsToDesktop = true;
    private string _documentDirectory = string.Empty;
    private bool _documentInTemp;
    private static readonly string DesktopDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    private static readonly string[] TempDirectories = GetTempDirectories();

    public bool IsCustomDestination => Destination == ExportDestination.Custom;
    public bool IsPresetDestination => !IsCustomDestination;
    public string ResolvedExportPath => ResolveExportPath(Destination, ExportPath);
    private bool RedirectTempDocument => ExportTempDocumentsToDesktop && _documentInTemp;
    public string DestinationPathHint => Destination == ExportDestination.DocumentFolder && RedirectTempDocument
        ? $"临时文档导出到桌面\n{ResolvedExportPath}"
        : ResolvedExportPath;

    private string ResolveExportPath(ExportDestination destination, string? customPath) => destination switch
    {
        ExportDestination.DocumentFolder => RedirectTempDocument ? DesktopDirectory : _documentDirectory,
        ExportDestination.Desktop => DesktopDirectory,
        _ => customPath?.Trim() ?? string.Empty
    };

    public string DestinationHint => DescribeDestinationHint(Destination);

    private string DescribeDestinationHint(ExportDestination destination) => destination switch
    {
        ExportDestination.DocumentFolder when RedirectTempDocument => "无法获取桌面位置，请选择自定义路径。",
        ExportDestination.DocumentFolder => HasDocument
            ? "文档尚未保存，请先保存，或改选桌面 / 自定义。"
            : "打开并保存文档后可用，也可选择桌面 / 自定义。",
        ExportDestination.Desktop => "无法获取桌面位置，请选择自定义路径。",
        _ => "请输入路径，或点击文件夹按钮选择。"
    };

    partial void OnExportTempDocumentsToDesktopChanged(bool value)
    {
        NotifyExportDestinationChanged();
        SaveBehaviorSettings();
    }

    [RelayCommand]
    private void SetExportTempDocumentsToDesktop(object? value) => ExportTempDocumentsToDesktop = ParseBooleanCommandParameter(value);

    partial void OnDestinationChanged(ExportDestination value)
    {
        OnPropertyChanged(nameof(IsCustomDestination));
        OnPropertyChanged(nameof(IsPresetDestination));
        NotifyExportDestinationChanged();
        QueueParameterMemory();
    }

    private void UpdateDocumentDirectory(string directory)
    {
        if (_documentDirectory == directory) return;
        _documentDirectory = directory;
        var path = NormalizeDirectory(directory);
        _documentInTemp = path is not null && TempDirectories.Any(temp =>
            path.Equals(temp, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        NotifyExportDestinationChanged();
    }

    private static string[] GetTempDirectories()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string?[] candidates =
        [
            Path.GetTempPath(),
            Environment.GetEnvironmentVariable("TEMP"),
            Environment.GetEnvironmentVariable("TMP"),
            localAppData.Length == 0 ? null : Path.Combine(localAppData, "Temp"),
            windows.Length == 0 ? null : Path.Combine(windows, "Temp")
        ];
        return candidates.Select(NormalizeDirectory).OfType<string>()
            .Where(path => path != Path.GetPathRoot(path))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    private void NotifyExportDestinationChanged()
    {
        OnPropertyChanged(nameof(ResolvedExportPath));
        OnPropertyChanged(nameof(DestinationPathHint));
        OnPropertyChanged(nameof(DestinationHint));
        RefreshCanExecute();
    }

    [RelayCommand]
    private void SetDestination(ExportDestination destination) => Destination = destination;

    private static string DescribeDefaultDestination(AppSettings settings)
    {
        var destination = settings.DefaultDestination ?? (string.IsNullOrWhiteSpace(settings.DefaultExportPath) ? ExportDestination.DocumentFolder : ExportDestination.Custom);
        return destination switch
        {
            ExportDestination.DocumentFolder => "文档所在目录 · 随当前文档切换",
            ExportDestination.Desktop => "桌面",
            _ => string.IsNullOrWhiteSpace(settings.DefaultExportPath) ? "自定义 · 尚未设置" : settings.DefaultExportPath
        };
    }
}
