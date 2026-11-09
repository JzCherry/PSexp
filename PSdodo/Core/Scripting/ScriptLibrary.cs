using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace PsExporter.Core.Scripting;

/// <summary>
/// 从嵌入资源加载内置脚本（.jsx 走 ExtendScript，.psjs 走 UXP）
/// </summary>
public static class ScriptLibrary
{
    private const string ResourcePrefix = "PsExporter.Resources.Scripts.";
    private static readonly Assembly _asm = typeof(ScriptLibrary).Assembly;

    // 支持的扩展名（按优先级）：.psjs 优先（UXP），.jsx 兜底（ExtendScript）
    private static readonly string[] _extensions = { ".psjs", ".jsx" };
    private static readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    /// <summary>读取脚本原文（未注入参数）</summary>
    public static string GetScript(string logicalName) => _cache.GetOrAdd(logicalName, LoadScript);

    private static string LoadScript(string logicalName)
    {
        // 先按扩展名逐个尝试精确匹配
        foreach (var ext in _extensions)
        {
            var resName = ResourcePrefix + logicalName.Replace('/', '.') + ext;
            var stream = _asm.GetManifestResourceStream(resName);
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                return ComposeHelpers(logicalName, reader.ReadToEnd());
            }
        }

        // 兜底：枚举所有资源名，按后缀匹配
        foreach (var ext in _extensions)
        {
            var suffix = "." + logicalName.Replace('/', '.') + ext;
            var all = _asm.GetManifestResourceNames();
            var match = Array.Find(all, n => n.EndsWith(suffix, StringComparison.Ordinal));
            if (match is not null)
            {
                var stream = _asm.GetManifestResourceStream(match);
                if (stream is not null)
                {
                    using var reader = new StreamReader(stream);
                    return ComposeHelpers(logicalName, reader.ReadToEnd());
                }
            }
        }

        throw new InvalidOperationException(
            $"找不到嵌入脚本资源: {logicalName}。可用资源: {string.Join(", ", _asm.GetManifestResourceNames())}");
    }

    private static string ComposeHelpers(string logicalName, string script)
    {
        if (logicalName == ScriptNames.ExportLayerBatch)
            script = GetScript("export/layer_targets") + "\n" + script;
        if (logicalName is ScriptNames.ExportLayerBatch or ScriptNames.ExportSlices
            or ScriptNames.ExportQuick or ScriptNames.ExportAllArtboards or ScriptNames.ExportSelectedArtboards)
            script = GetScript("export/export_names") + "\n" + script;
        return script;
    }

    /// <summary>正则提取脚本中所有 {{NAME}} 占位符（去重）</summary>
    public static IReadOnlyCollection<string> ExtractPlaceholders(string script)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var rx = new Regex(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}");
        foreach (Match m in rx.Matches(script))
            set.Add(m.Groups[1].Value);
        return set;
    }
}
