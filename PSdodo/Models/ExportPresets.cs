namespace PsExporter.Models;

public enum ExportPresetCategory { Quick, Artboards, Layers, Slices }

/// <summary>User-editable parameters only. Never store document identity, layer IDs or runtime results.</summary>
public sealed record ExportPresetParameters
{
    public ExportMode Mode { get; init; } = ExportMode.QuickExport;
    public ExportFormat Format { get; init; } = ExportFormat.Jpg;
    public int Quality { get; init; } = 80;
    public bool AutoRename { get; init; } = true;
    public ExportDestination Destination { get; init; } = ExportDestination.DocumentFolder;
    public string CustomPath { get; init; } = "";
    public string ImageName { get; init; } = "";
    public string SlicePrefix { get; init; } = "";
    public string SliceFolder { get; init; } = "";
    public ExportPathOption ArtboardPath { get; init; } = ExportPathOption.ImageOnly;
    public bool LayerRecursive { get; init; }
    public bool LayerSelectedOnly { get; init; } = true;
    public bool LayerWithDocumentName { get; init; } = true;
    public bool UseDocumentName { get; init; }
}

public sealed record ExportPreset
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public ExportPresetCategory Category { get; init; }
    public bool IncludeDestination { get; init; } = true;
    public bool IncludeNaming { get; init; } = true;
    public ExportPresetParameters Parameters { get; init; } = new();
}

public sealed class ExportPresetWorkspace
{
    public ExportPresetCategory Category { get; set; }
    public string? SelectedPresetId { get; set; }
    public ExportPresetParameters Parameters { get; set; } = new();
}

public sealed class ExportPresetsState
{
    public int Version { get; set; } = 1;
    public List<ExportPreset> Items { get; set; } = [];
    public List<ExportPresetWorkspace> Workspaces { get; set; } = [];
}
