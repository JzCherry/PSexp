using System.Text.Json;
using PsExporter.Models;

namespace PsExporter.Services;

public static class ExportPresetCatalog
{
    public const int MaximumItems = 200;
    public const int MaximumNameLength = 40;
    public const int MaximumImportBytes = 1024 * 1024;
    private const string FileKind = "PSexp.export-presets";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static ExportPresetCategory CategoryFor(ExportMode mode) => mode switch
    {
        ExportMode.AllArtboards or ExportMode.SelectedArtboards => ExportPresetCategory.Artboards,
        ExportMode.Layers => ExportPresetCategory.Layers,
        ExportMode.Slices => ExportPresetCategory.Slices,
        _ => ExportPresetCategory.Quick
    };
    public static ExportMode DefaultMode(ExportPresetCategory category) => category switch
    {
        ExportPresetCategory.Artboards => ExportMode.AllArtboards,
        ExportPresetCategory.Layers => ExportMode.Layers,
        ExportPresetCategory.Slices => ExportMode.Slices,
        _ => ExportMode.QuickExport
    };
    public static string CategoryName(ExportPresetCategory category) => category switch
    {
        ExportPresetCategory.Artboards => "画板导出",
        ExportPresetCategory.Layers => "图层导出",
        ExportPresetCategory.Slices => "切片导出",
        _ => "快速导出"
    };

    /// <summary>命令行里写板块用的标识。板块与英文标识的对应只在这里有一份。</summary>
    public static string CategoryToken(ExportPresetCategory category) => category switch
    {
        ExportPresetCategory.Quick => "quick",
        ExportPresetCategory.Artboards => "artboards",
        ExportPresetCategory.Layers => "layers",
        _ => "slices"
    };

    public static bool TryParseCategoryToken(string? token, out ExportPresetCategory category)
    {
        foreach (var value in Enum.GetValues<ExportPresetCategory>())
        {
            if (string.Equals(CategoryToken(value), token, StringComparison.OrdinalIgnoreCase))
            {
                category = value;
                return true;
            }
        }
        category = default;
        return false;
    }

    /// <summary>给指令说明用的板块列举；中文叫法由界面上的分段按钮自己承担，这里不重复。</summary>
    public static string CategoryTokenList()
        => string.Join(" / ", Enum.GetValues<ExportPresetCategory>().Select(CategoryToken));

    /// <summary>图片规格读数。预设下拉与导出指令窗口共用，避免同一份参数出现两种写法。</summary>
    public static string DescribeFormat(ExportPresetParameters p)
        => p.Format == ExportFormat.Png ? "PNG" : $"JPG · {p.Quality}%";

    /// <summary>导出对象读数；快速导出与切片没有对象选择，返回空串表示这一项不显示。</summary>
    public static string DescribeScope(ExportPresetCategory category, ExportPresetParameters p) => category switch
    {
        ExportPresetCategory.Artboards => p.Mode == ExportMode.SelectedArtboards ? "选中画板" : "全部画板",
        ExportPresetCategory.Layers => (p.LayerRecursive ? "图层素材" : "画布合成")
            + (p.LayerScopeSelectedOnly == true ? " · 选中" : " · 全部"),
        _ => ""
    };

    /// <summary>输出位置短标签；自定义时完整路径另行放进悬停提示，不进读数。</summary>
    public static string DescribeDestination(ExportPresetParameters p) => p.Destination switch
    {
        ExportDestination.Desktop => "桌面",
        ExportDestination.Custom => "自定义",
        _ => "文档位置"
    };

    /// <summary>命名读数；留空时返回空串，因为留空的含义随板块不同，不在这里替它承诺结果。</summary>
    public static string DescribeNaming(ExportPresetCategory category, ExportPresetParameters p) => category switch
    {
        ExportPresetCategory.Quick when !string.IsNullOrWhiteSpace(p.ImageName) => "图片名 " + p.ImageName,
        ExportPresetCategory.Quick => "",
        _ => string.IsNullOrWhiteSpace(p.FilePrefix) ? "" : "前缀 " + p.FilePrefix
    };
    public static ExportPreset BuiltIn(ExportPresetCategory category) => new()
    {
        Id = "default:" + category, Name = "默认预设", Category = category,
        Parameters = new()
        {
            Mode = DefaultMode(category),
            SaveAndCloseAfterExport = false,
            FilePrefix = category == ExportPresetCategory.Quick ? null : "",
            UseDocumentNameAsFallbackPrefix = category == ExportPresetCategory.Quick ? null : false,
            LayerPrefixMode = category == ExportPresetCategory.Layers ? LayerPrefixMode.None : null
        }
    };
    public static bool IsBuiltIn(string? id) => id?.StartsWith("default:", StringComparison.Ordinal) == true;

    public static ExportPresetParameters ParametersForPreset(ExportPreset preset)
    {
        var p = Normalize(preset.Parameters, preset.Category);
        // Omitted and unrelated fields must not leak into portable preset files.
        return new()
        {
            Mode = p.Mode, Format = p.Format, Quality = p.Quality, AutoRename = p.AutoRename,
            SaveAndCloseAfterExport = p.SaveAndCloseAfterExport,
            Destination = preset.IncludeDestination ? p.Destination : ExportDestination.DocumentFolder,
            CustomPath = preset.IncludeDestination && p.Destination == ExportDestination.Custom ? p.CustomPath : "",
            ImageName = preset.IncludeNaming && preset.Category is ExportPresetCategory.Quick or ExportPresetCategory.Layers ? p.ImageName : "",
            FilePrefix = preset.IncludeNaming && preset.Category != ExportPresetCategory.Quick ? p.FilePrefix : null,
            UseDocumentNameAsFallbackPrefix = preset.IncludeNaming ? p.UseDocumentNameAsFallbackPrefix : null,
            LayerPrefixMode = preset.IncludeNaming && preset.Category == ExportPresetCategory.Layers ? p.LayerPrefixMode : null,
            SlicePrefix = preset.IncludeNaming && preset.Category == ExportPresetCategory.Slices ? p.SlicePrefix : "",
            SliceFolder = preset.Category == ExportPresetCategory.Slices ? p.SliceFolder : "",
            ArtboardPath = preset.Category == ExportPresetCategory.Artboards ? p.ArtboardPath : ExportPathOption.ImageOnly,
            LayerRecursive = preset.Category == ExportPresetCategory.Layers && p.LayerRecursive,
            LayerSelectedOnly = preset.Category != ExportPresetCategory.Layers || p.LayerSelectedOnly,
            LayerTopLevelSelectedOnly = preset.Category == ExportPresetCategory.Layers && p.LayerTopLevelSelectedOnly,
            LayerScopeSelectedOnly = preset.Category == ExportPresetCategory.Layers ? p.LayerScopeSelectedOnly : null,
            LayerWithDocumentName = !preset.IncludeNaming || preset.Category != ExportPresetCategory.Layers || p.LayerWithDocumentName,
            UseDocumentName = preset.IncludeNaming && preset.Category == ExportPresetCategory.Layers && p.UseDocumentName
        };
    }

    public static ExportPresetParameters Normalize(ExportPresetParameters? value, ExportPresetCategory category)
    {
        value ??= new();
        var scope = value.LayerScopeSelectedOnly ?? (value.LayerRecursive ? value.LayerSelectedOnly : value.LayerTopLevelSelectedOnly);
        var prefix = category switch
        {
            ExportPresetCategory.Layers => LayerPrefixNaming.ResolveInput(value.FilePrefix, value.LayerPrefixMode, value.ImageName, value.LayerWithDocumentName, value.UseDocumentName),
            ExportPresetCategory.Artboards => value.FilePrefix ?? "",
            ExportPresetCategory.Slices => value.FilePrefix ?? value.SlicePrefix ?? "",
            _ => ""
        };
        var prefixMode = category == ExportPresetCategory.Layers
            ? string.IsNullOrWhiteSpace(prefix) ? LayerPrefixMode.None : LayerPrefixMode.Custom : (LayerPrefixMode?)null;
        return value with
        {
            Mode = Enum.IsDefined(value.Mode) && CategoryFor(value.Mode) == category ? value.Mode : DefaultMode(category),
            Format = Enum.IsDefined(value.Format) ? value.Format : ExportFormat.Jpg,
            Quality = Math.Clamp(value.Quality, 1, 100),
            Destination = Enum.IsDefined(value.Destination) ? value.Destination : ExportDestination.DocumentFolder,
            ArtboardPath = Enum.IsDefined(value.ArtboardPath) ? value.ArtboardPath : ExportPathOption.ImageOnly,
            CustomPath = value.CustomPath ?? "", ImageName = category == ExportPresetCategory.Layers ? prefix : value.ImageName ?? "",
            FilePrefix = category == ExportPresetCategory.Quick ? null : prefix,
            UseDocumentNameAsFallbackPrefix = category == ExportPresetCategory.Quick ? null : value.UseDocumentNameAsFallbackPrefix,
            SlicePrefix = category == ExportPresetCategory.Slices ? prefix : value.SlicePrefix ?? "", SliceFolder = value.SliceFolder ?? "",
            LayerPrefixMode = prefixMode,
            LayerScopeSelectedOnly = category == ExportPresetCategory.Layers ? scope : null,
            LayerSelectedOnly = category == ExportPresetCategory.Layers ? scope : value.LayerSelectedOnly,
            LayerTopLevelSelectedOnly = category == ExportPresetCategory.Layers ? scope : value.LayerTopLevelSelectedOnly,
            LayerWithDocumentName = category == ExportPresetCategory.Layers ? prefixMode == LayerPrefixMode.DocumentName : value.LayerWithDocumentName,
            UseDocumentName = category == ExportPresetCategory.Layers ? prefixMode == LayerPrefixMode.DocumentName : value.UseDocumentName
        };
    }

    /// <summary>Comparison is scoped to fields actually owned by this preset.</summary>
    public static bool Matches(ExportPreset preset, ExportPresetParameters current)
    {
        var expected = Normalize(preset.Parameters, preset.Category);
        current = Normalize(current, preset.Category);
        if (expected.Format != current.Format || expected.Quality != current.Quality || expected.AutoRename != current.AutoRename) return false;
        if (expected.SaveAndCloseAfterExport != current.SaveAndCloseAfterExport) return false;
        if (preset.IncludeDestination && (expected.Destination != current.Destination
            || (expected.Destination == ExportDestination.Custom && expected.CustomPath != current.CustomPath))) return false;
        if (preset.Category == ExportPresetCategory.Artboards && (expected.Mode != current.Mode || expected.ArtboardPath != current.ArtboardPath)) return false;
        if (preset.Category == ExportPresetCategory.Layers && (expected.LayerRecursive != current.LayerRecursive || expected.LayerScopeSelectedOnly != current.LayerScopeSelectedOnly)) return false;
        if (preset.Category == ExportPresetCategory.Slices && expected.SliceFolder != current.SliceFolder) return false;
        if (!preset.IncludeNaming) return true;
        return preset.Category switch
        {
            ExportPresetCategory.Quick => expected.ImageName == current.ImageName,
            ExportPresetCategory.Layers or ExportPresetCategory.Artboards or ExportPresetCategory.Slices => expected.FilePrefix == current.FilePrefix
                && expected.UseDocumentNameAsFallbackPrefix == current.UseDocumentNameAsFallbackPrefix,
            _ => true
        };
    }

    public static ExportPresetsState NormalizeState(ExportPresetsState? source)
    {
        var result = new ExportPresetsState { DefaultsInitialized = source?.DefaultsInitialized ?? false };
        if (source is null) return result;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var preset in (source.Items ?? []).OfType<ExportPreset>().Take(MaximumItems))
        {
            if (!Enum.IsDefined(preset.Category) || string.IsNullOrWhiteSpace(preset.Id) || !ids.Add(preset.Id)) continue;
            var name = preset.Name?.Trim() ?? "";
            if (name.Length == 0 || name.Length > MaximumNameLength || name.Any(char.IsControl)) continue;
            result.Items.Add(preset with { Name = UniqueName(name, preset.Category, result.Items), Parameters = ParametersForPreset(preset) });
        }
        foreach (var workspace in (source.Workspaces ?? []).OfType<ExportPresetWorkspace>())
        {
            if (!Enum.IsDefined(workspace.Category) || result.Workspaces.Any(item => item.Category == workspace.Category)) continue;
            var selected = workspace.SelectedPresetId;
            if (!(IsBuiltIn(selected) && !source.DefaultsInitialized) && !result.Items.Any(item => item.Category == workspace.Category && item.Id == selected)) selected = null;
            result.Workspaces.Add(new() { Category = workspace.Category, SelectedPresetId = selected, Parameters = Normalize(workspace.Parameters, workspace.Category) });
        }
        return result;
    }

    public static ExportPreset CompleteLegacyPreset(ExportPreset item, ExportPresetParameters workspace)
    {
        var p = item.Parameters;
        if (!item.IncludeDestination) p = p with { Destination = workspace.Destination, CustomPath = workspace.CustomPath };
        if (!item.IncludeNaming) p = p with { ImageName = workspace.ImageName, SlicePrefix = workspace.SlicePrefix,
            UseDocumentNameAsFallbackPrefix = workspace.UseDocumentNameAsFallbackPrefix,
            LayerWithDocumentName = workspace.LayerWithDocumentName, UseDocumentName = workspace.UseDocumentName, LayerPrefixMode = workspace.LayerPrefixMode, FilePrefix = workspace.FilePrefix };
        var complete = item with { IncludeDestination = true, IncludeNaming = true, Parameters = p };
        return complete with { Parameters = ParametersForPreset(complete) };
    }

    public static string UniqueName(string name, ExportPresetCategory category, IEnumerable<ExportPreset> items)
    {
        name = name.Trim();
        var existing = items.Where(item => item.Category == category).Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(name)) return name;
        for (var index = 2; ; index++)
        {
            var suffix = $" ({index})";
            var candidate = name[..Math.Min(name.Length, MaximumNameLength - suffix.Length)] + suffix;
            if (!existing.Contains(candidate)) return candidate;
        }
    }

    private sealed class PresetFile
    {
        public string Kind { get; set; } = FileKind;
        public int Version { get; set; } = 1;
        public List<ExportPreset> Presets { get; set; } = [];
    }
    public static string Export(IEnumerable<ExportPreset> presets) => JsonSerializer.Serialize(new PresetFile
        { Presets = presets.Select(item => item with { Parameters = ParametersForPreset(item) }).ToList() }, JsonOptions);

    public static IReadOnlyList<ExportPreset> ParseImport(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumImportBytes) throw new InvalidDataException("预设文件超过 1 MB。");
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("kind", out var kind)
            || kind.ValueKind != JsonValueKind.String || kind.GetString() != FileKind
            || !document.RootElement.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1)
            throw new InvalidDataException("这不是支持的 PSexp 预设文件。");
        var file = JsonSerializer.Deserialize<PresetFile>(json, JsonOptions) ?? throw new InvalidDataException("无法读取预设文件。");
        if (file.Kind != FileKind || file.Version != 1) throw new InvalidDataException("这不是支持的 PSexp 预设文件。");
        if (file.Presets is null || file.Presets.Count == 0 || file.Presets.Count > MaximumItems) throw new InvalidDataException("预设数量应为 1–200 个。");
        // Validate everything before mutating the live catalog. Imports never replace existing IDs.
        var parsed = new List<ExportPreset>();
        foreach (var preset in file.Presets)
        {
            if (preset is null || !Enum.IsDefined(preset.Category) || preset.Parameters is null
                || string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Trim().Length > MaximumNameLength
                || preset.Name.Any(char.IsControl) || !Enum.IsDefined(preset.Parameters.Mode)
                || CategoryFor(preset.Parameters.Mode) != preset.Category || !Enum.IsDefined(preset.Parameters.Format)
                || preset.Parameters.Quality is < 1 or > 100 || !Enum.IsDefined(preset.Parameters.Destination)
                || !Enum.IsDefined(preset.Parameters.ArtboardPath)
                || (preset.Parameters.LayerPrefixMode is { } prefixMode && !Enum.IsDefined(prefixMode))) throw new InvalidDataException("预设包含无效名称、分组或导出参数，未导入任何内容。");
            parsed.Add(preset with { Id = Guid.NewGuid().ToString("N"), Name = preset.Name.Trim(), Parameters = ParametersForPreset(preset) });
        }
        return parsed;
    }
}
