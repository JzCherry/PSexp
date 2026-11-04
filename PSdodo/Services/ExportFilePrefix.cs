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
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(value.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).TrimEnd('.', ' ');
        return name.Length == 0 ? "未命名" : name;
    }
}
