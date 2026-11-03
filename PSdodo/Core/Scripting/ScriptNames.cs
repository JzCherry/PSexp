namespace PsExporter.Core.Scripting;

/// <summary>
/// 内置脚本的逻辑名称（与 Resources/Scripts 目录结构对应）
/// </summary>
public static class ScriptNames
{
    // 探针
    public const string ProbeDocumentInfo = "probe/document_info";
    public const string ProbeListArtboards = "probe/list_artboards";

    // 导出
    public const string ExportQuick = "export/quick_export";
    public const string ExportAllArtboards = "export/all_artboards";
    public const string ExportSelectedArtboards = "export/selected_artboards";
    public const string ExportLayers = "export/layers";
    public const string ExportSlices = "export/native_slices";
    public const string ExportLayerBatch = "export/layer_batch";
}
