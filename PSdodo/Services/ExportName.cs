using System.Text;
using System.Text.RegularExpressions;

namespace PsExporter.Services;

/// <summary>Normalize one filename component while preserving valid Unicode, including emoji.</summary>
public static class ExportName
{
    public static string Normalize(string? value, string fallback = "未命名")
    {
        var result = new StringBuilder();
        var text = value ?? "";
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                result.Append(ch).Append(text[++i]);
                continue;
            }
            result.Append(char.IsControl(ch) || char.IsSurrogate(ch) || ch is '\u2028' or '\u2029' or '\ufffd'
                || "\\/:*?\"<>|".Contains(ch) ? '_' : ch);
        }
        var name = result.ToString().Trim().TrimEnd('.', ' ');
        if (name.Length == 0) return fallback;
        if (Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase))
            name = "_" + name;
        return name;
    }
}
