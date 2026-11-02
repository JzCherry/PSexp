namespace PsExporter.Services;

public static class ExportFilePrefix
{
    /// <summary>The same prefix is sent to all three batch-export paths.</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(value.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).TrimEnd('.', ' ');
        return name.Length == 0 ? "未命名" : name;
    }
}
