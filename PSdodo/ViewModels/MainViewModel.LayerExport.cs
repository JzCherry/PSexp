using System.Text.Json;

namespace PsExporter.ViewModels;

public partial class MainViewModel
{
    private static string ReadLayerExportWarning(string? receipt)
    {
        if (string.IsNullOrWhiteSpace(receipt)) return "";
        try
        {
            using var json = JsonDocument.Parse(receipt);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("warning", out var warning) && warning.ValueKind == JsonValueKind.String
                ? warning.GetString() ?? "" : "";
        }
        catch (JsonException) { return ""; }
    }
}
