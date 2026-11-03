using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PsExporter.Models;

namespace PsExporter.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] private ExportDestination _destination = ExportDestination.DocumentFolder;
    private string _documentDirectory = string.Empty;
    private static readonly string DesktopDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public bool IsCustomDestination => Destination == ExportDestination.Custom;
    public bool IsPresetDestination => !IsCustomDestination;
    public string ResolvedExportPath => ResolveExportPath(Destination, ExportPath);

    private string ResolveExportPath(ExportDestination destination, string? customPath) => destination switch
    {
        ExportDestination.DocumentFolder => _documentDirectory,
        ExportDestination.Desktop => DesktopDirectory,
        _ => customPath?.Trim() ?? string.Empty
    };

    public string DestinationHint => DescribeDestinationHint(Destination);

    private string DescribeDestinationHint(ExportDestination destination) => destination switch
    {
        ExportDestination.DocumentFolder => HasDocument
            ? "文档尚未保存，请先保存，或改选桌面 / 自定义。"
            : "打开并保存文档后可用，也可选择桌面 / 自定义。",
        ExportDestination.Desktop => "无法获取桌面位置，请选择自定义路径。",
        _ => "请输入路径，或点击文件夹按钮选择。"
    };

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
        NotifyExportDestinationChanged();
    }

    private void NotifyExportDestinationChanged()
    {
        OnPropertyChanged(nameof(ResolvedExportPath));
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
