namespace PsExporter.Services;

public static class ExportFilePrefix
{
    public static string Resolve(string? value, string documentName, bool useDocumentName)
        => Normalize(useDocumentName && string.IsNullOrWhiteSpace(value)
            ? Path.GetFileNameWithoutExtension(documentName)
            : value);

    /// <summary>The same prefix is sent to all three batch-export paths.</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return ExportName.Normalize(value);
    }
}
