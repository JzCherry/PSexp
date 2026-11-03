using PsExporter.Models;

namespace PsExporter.Core.Automation;

internal sealed record ExportInvocation(ExportPresetCategory? Category = null, string? Preset = null)
{
    internal static bool TryParse(string[] args, out ExportInvocation? invocation)
    {
        invocation = null;
        if (args.Length == 0) return true;
        if (args is ["--export"])
            invocation = new();
        else if (args is ["--preset-id", var id] && IsValidReference(id))
            invocation = new(Preset: id);
        else if (args is ["--preset", var category, var name] && IsValidReference(name))
        {
            ExportPresetCategory? parsed = category?.ToLowerInvariant() switch
            {
                "quick" => ExportPresetCategory.Quick,
                "artboards" => ExportPresetCategory.Artboards,
                "layers" => ExportPresetCategory.Layers,
                "slices" => ExportPresetCategory.Slices,
                _ => null
            };
            if (parsed.HasValue) invocation = new(parsed, name);
        }
        return invocation is not null;
    }

    internal static string BuildPowerShellCommand(string executable, string? presetId = null)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        return "& " + Quote(executable) + (presetId is null ? " --export" : " --preset-id " + Quote(presetId));
    }

    private static bool IsValidReference(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl);
}
