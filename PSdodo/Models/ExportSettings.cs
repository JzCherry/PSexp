namespace PsExporter.Models;

/// <summary>一次导出任务的用户参数</summary>
public sealed class ExportSettings
{
    public const double MinimumQuality = 1d;

    public const double MaximumQuality = 100d;

    public const double QualityStep = 1d;

    public string ExportPath { get; set; } = string.Empty;

    /// <summary>当前 Photoshop 文档名，用于画板导出子目录命名</summary>
    public string DocumentName { get; set; } = string.Empty;

    public ExportFormat Format { get; set; } = ExportFormat.Jpg;

    /// <summary>JPG: 0-100；脚本里 PNG 时此值透传给 PS 快速导出</summary>
    public int Quality { get; set; } = 80;

    /// <summary>true=不覆盖(自动重命名)；false=覆盖</summary>
    public bool AutoRename { get; set; } = true;

    /// <summary>切片导出时的文件名前缀</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Batch-export prefix; empty keeps the original filename.</summary>
    public string FilePrefix { get; set; } = string.Empty;

    /// <summary>切片子文件夹名，空值直接保存到输出目录。</summary>
    public string SliceFolderName { get; set; } = string.Empty;

    /// <summary>快速导出的完整图片名，或图层导出的文件名前缀</summary>
    public string ImageName { get; set; } = string.Empty;

    /// <summary>图层导出时是否使用文档名作为默认前缀</summary>
    public bool LayerExportWithDocName { get; set; } = true;
    public bool LayerExportRecursive { get; set; }
    public bool LayerExportSelectedOnly { get; set; } = true;
    public bool LayerTopLevelSelectedOnly { get; set; }

    /// <summary>兼容旧设置字段；当前导出结果统一由软件内状态栏展示，不触发 Photoshop 提示。</summary>
    public bool LayerExportShowHint { get; set; }

    /// <summary>画板导出路径选项</summary>
    public ExportPathOption ExportPathOption { get; set; } = ExportPathOption.ImageOnly;

    /// <summary>已知的预期输出数量；未知时为 null</summary>
    public int? ExpectedFileCount { get; set; }

    /// <summary>选择画板导出时选中的画板 ID 列表</summary>
    public int[]? SelectedArtboardIds { get; set; }
}
