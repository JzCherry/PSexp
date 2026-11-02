using PsExporter.Models;

namespace PsExporter.Services;

public static class LayerPrefixNaming
{
    /// <summary>Keep old effective naming: force document, manual text, document fallback, or no prefix.</summary>
    public static LayerPrefixMode ResolveMode(LayerPrefixMode? explicitMode, string? customPrefix,
        bool legacyDocumentFallback, bool legacyForceDocument)
    {
        if (explicitMode is { } mode && Enum.IsDefined(mode)) return mode;
        if (legacyForceDocument) return LayerPrefixMode.DocumentName;
        if (!string.IsNullOrEmpty(customPrefix)) return LayerPrefixMode.Custom;
        return legacyDocumentFallback ? LayerPrefixMode.DocumentName : LayerPrefixMode.None;
    }
    /// <summary>Legacy automatic document rules are retired. Preserve only an active, explicit text prefix.</summary>
    public static string ResolveInput(string? explicitPrefix, LayerPrefixMode? legacyMode, string? legacyText,
        bool legacyDocumentFallback, bool legacyForceDocument)
        => explicitPrefix ?? (ResolveMode(legacyMode, legacyText, legacyDocumentFallback, legacyForceDocument) == LayerPrefixMode.Custom
            ? legacyText ?? "" : "");
}
