using PsExporter.Models;
using PsExporter.Services;
using System.Text.RegularExpressions;

namespace PsExporter.Core.Automation;

/// <summary>外部指令的形态。执行时读哪一份参数，完全由它决定。</summary>
public enum ExportInvocationKind
{
    /// <summary>执行那一刻主界面上的参数，不引用任何预设。</summary>
    CurrentInterface,

    /// <summary>预设已保存的参数。预设改名后仍然有效，删掉预设后失效。</summary>
    PresetById,

    /// <summary>按板块与预设名称查找。预设改名后这条指令就失效，所以界面不产出它，只供外部工具手写。</summary>
    PresetByName
}

/// <summary>一条可序列化、可复制、可执行的外部指令。构建与解析互为镜像，测试断言二者往返一致。</summary>
public sealed record ExportInvocation(
    ExportInvocationKind Kind = ExportInvocationKind.CurrentInterface,
    ExportPresetCategory? Category = null,
    string? Preset = null)
{
    public const string ExportFlag = "--export";
    public const string PresetIdFlag = "--preset-id";
    public const string PresetFlag = "--preset";

    public static ExportInvocation ForPresetId(string presetId)
        => new(ExportInvocationKind.PresetById, Preset: presetId);

    public static ExportInvocation ForPresetName(ExportPresetCategory category, string name)
        => new(ExportInvocationKind.PresetByName, category, name);

    /// <summary>形态、写法、说明，以及窗口是否会产出这一条。界面文案与命令行解析共用这一份清单。</summary>
    public static IReadOnlyList<ExportCommandForm> Forms { get; } =
    [
        new(ExportInvocationKind.CurrentInterface, ExportFlag,
            "使用运行指令时的界面设置。复制指令时不会保存一份参数快照。", true),
        new(ExportInvocationKind.PresetById, $"{PresetIdFlag} <预设 ID>",
            "按 ID 读取预设已保存的参数；预设改名不影响，删掉预设后这条指令失效。", true),
        new(ExportInvocationKind.PresetByName, $"{PresetFlag} <板块> <名称>",
            $"按板块和名称查找预设，供外部工具手写；预设改名后这条指令失效。板块可用 {ExportPresetCatalog.CategoryTokenList()}。", false)
    ];

    public static bool TryParse(string[] args, out ExportInvocation? invocation)
    {
        invocation = null;
        if (args.Length == 0) return true;
        if (args is [ExportFlag]) invocation = new();
        else if (args is [PresetIdFlag, var id] && IsValidReference(id)) invocation = ForPresetId(id);
        else if (args is [PresetFlag, var token, var name] && IsValidReference(name)
                 && ExportPresetCatalog.TryParseCategoryToken(token, out var category))
            invocation = ForPresetName(category, name);
        return invocation is not null;
    }

    /// <summary>可直接粘贴到 Windows「运行」的程序路径与参数。</summary>
    public static string BuildWindowsCommandLine(string executable, ExportInvocation invocation)
    {
        string[] args = invocation.Kind switch
        {
            ExportInvocationKind.CurrentInterface => [ExportFlag],
            ExportInvocationKind.PresetById when IsValidReference(invocation.Preset) => [PresetIdFlag, invocation.Preset!],
            ExportInvocationKind.PresetByName when invocation.Category is { } category && IsValidReference(invocation.Preset)
                => [PresetFlag, ExportPresetCatalog.CategoryToken(category), invocation.Preset!],
            _ => throw new ArgumentException("指令形态与参数不匹配。", nameof(invocation))
        };
        return QuoteWindowsArgument(executable) + " " + string.Join(' ', args.Select(arg =>
            arg.Any(char.IsWhiteSpace) || arg.Contains('"') ? QuoteWindowsArgument(arg) : arg));
    }

    private static string QuoteWindowsArgument(string value)
    {
        // 双引号前的反斜杠要加倍；参数自身的双引号再加一个反斜杠。
        var escaped = Regex.Replace(value, "(\\\\*)(\"|\\z)", match =>
        {
            var quote = match.Groups[2].Value;
            var backslashes = match.Groups[1].Length * 2 + quote.Length;
            return new string('\\', backslashes) + quote;
        });
        return "\"" + escaped + "\"";
    }

    private static bool IsValidReference(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl);
}

/// <summary>指令清单里的一行。</summary>
/// <param name="Syntax">写法，供界面原样展示。</param>
/// <param name="Description">执行时读哪一份参数、什么时候失效。</param>
/// <param name="ProducedByWindow">导出指令窗口是否会产出这一条；false 表示只供外部工具手写。</param>
public sealed record ExportCommandForm(ExportInvocationKind Kind, string Syntax, string Description, bool ProducedByWindow);
