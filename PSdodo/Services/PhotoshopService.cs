using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PsExporter.Core.PsConnection;
using PsExporter.Core.Process;
using PsExporter.Core.Scripting;
using PsExporter.Models;

namespace PsExporter.Services;

public sealed partial class PhotoshopService : IPhotoshopService
{
    private readonly IPsBridge _bridge;
    private readonly IPhotoshopDetector _detector;

    public PhotoshopService(IPsBridge bridge, IPhotoshopDetector detector, UxpScriptBridge? probeUxpBridge = null, PsExporter.Core.Logging.FileLogger? probeLogger = null)
    {
        _bridge = bridge;
        _detector = detector;
        _probeUxpBridge = probeUxpBridge;
        _probeLogger = probeLogger;
    }

    public bool IsBridgeAvailable => _bridge.IsAvailable;

    public PhotoshopProcessInfo DetectProcess() => _detector.Detect();

    public Task<bool> TryLaunchAsync(string? explicitPath, CancellationToken ct = default)
        => _detector.TryLaunchAsync(explicitPath, ct);

    public async Task<PsDocumentInfo> GetDocumentInfoAsync(CancellationToken ct = default)
    {
        var (doc, _) = await ProbeDocumentInfoAsync(ct);
        return doc;
    }

    public async Task<(PsDocumentInfo Doc, PsBridgeResult Result)> ProbeDocumentInfoAsync(CancellationToken ct = default)
    {
        var (document, res) = await ProbeDocumentInfoQuietlyAsync(ct);
        if (res.Success || _probeUxpBridge is null) return (document, res);
        // Only explicit export preflight may use this fallback. Refresh commands and activation use the quiet path.
        _probeLogger?.Warn("导出前 COM 文档识别不可用，改用 UXP：" + res.Error);
        return await ProbeWithUxpAsync(ct);
    }

    public async Task<(PsDocumentInfo Doc, PsBridgeResult Result)> ProbeDocumentInfoQuietlyAsync(CancellationToken ct = default)
    {
        // DoJavaScript executes inside the attached Photoshop instance; no ShellExecute, .psjs or focus activation.
        var res = await ExecuteScriptAsync(ScriptNames.ProbeDocumentInfo, EmptyArgs, ct);
        if (!res.Success) return (PsDocumentInfo.Empty, res);
        var doc = PsDocumentInfo.Parse(res.ReturnValue);
        if (doc is null)
            return (PsDocumentInfo.Empty, new PsBridgeResult
            {
                Success = false, ReturnValue = res.ReturnValue, Logs = res.Logs,
                Error = new InvalidDataException("Photoshop 文档探测返回了无效数据，不能据此判定没有文档")
            });
        return (doc, res);
    }

    public async Task<List<ArtboardInfo>> ListArtboardsAsync(CancellationToken ct = default)
    {
        var res = await ExecuteScriptAsync(ScriptNames.ProbeListArtboards, EmptyArgs, ct);
        if (!res.Success) return new List<ArtboardInfo>();
        return ArtboardInfo.ParseList(res.ReturnValue);
    }

    public async Task<PsBridgeResult> ExecuteScriptAsync(
        string scriptName, IReadOnlyDictionary<string, string> args, CancellationToken ct = default)
    {
        var template = ScriptLibrary.GetScript(scriptName);
        var script = ScriptParameterInjector.Inject(template, args);
        try
        {
            return await _bridge.ExecuteAsync(script, null, ct);
        }
        catch (PsNotConnectedException ex)
        {
            return new PsBridgeResult { Success = false, Error = ex, ReturnValue = string.Empty, Logs = [] };
        }
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyArgs =
        new Dictionary<string, string>();
}
