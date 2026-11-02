using System.Text;
using System.Text.Json;
using PsExporter.Core.Logging;
using PsExporter.Core.Scripting;

namespace PsExporter.Services;

/// <summary>Photoshop notifiers -> tiny signal file -> OS notification. No idle polling or document reads.</summary>
public sealed class PhotoshopDocumentEvents : IDisposable
{
    private readonly IPhotoshopService _photoshop;
    private readonly FileLogger _logger;
    private readonly string _directory;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly object _state = new();
    private readonly Timer _debounce;
    private FileSystemWatcher? _watcher;
    private FileStream? _lease;
    private bool _enabled = true, _disposed, _hasRegistration, _watcherHealthy;
    private int _registeredPid;
    private long _version;
    private Task _cleanup = Task.CompletedTask;

    public event EventHandler? Changed;
    public long Version => Interlocked.Read(ref _version);
    public bool IsSubscribed { get { lock (_state) return _registeredPid != 0 && _lease is not null && _watcherHealthy; } }

    public PhotoshopDocumentEvents(IPhotoshopService photoshop, FileLogger logger, string? directory = null)
    {
        _photoshop = photoshop;
        _logger = logger;
        _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PSexp", "DocumentEvents");
        _debounce = new Timer(_ => Notify(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void SetEnabled(bool enabled)
    {
        lock (_state)
        {
            if (_disposed) return;
            _enabled = enabled;
            if (!enabled) Deactivate();
        }
        if (!enabled) _cleanup = UnsubscribeAsync();
    }

    public async Task EnsureSubscribedAsync(int processId)
    {
        if (processId <= 0 || !OperatingSystem.IsWindows()) return;
        await _operations.WaitAsync();
        try
        {
            lock (_state)
            {
                if (!_enabled || _disposed || (_registeredPid == processId && _lease is not null && _watcherHealthy)) return;
                Deactivate();
                Directory.CreateDirectory(_directory);
                var handler = ScriptParameterInjector.Inject(ScriptLibrary.GetScript("probe/document_change_handler"),
                    new Dictionary<string, string>
                    {
                        ["LEASE_PATH"] = ScriptParameterInjector.EscapeJsString(Path.Combine(_directory, "active.lock")),
                        ["SIGNAL_PATH"] = ScriptParameterInjector.EscapeJsString(Path.Combine(_directory, "changed.signal"))
                    });
                File.WriteAllText(Path.Combine(_directory, "notifier.jsx"), handler, new UTF8Encoding(false));
                _lease = new FileStream(Path.Combine(_directory, "active.lock"), FileMode.Create, FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.DeleteOnClose);
                _lease.Write(Encoding.ASCII.GetBytes(Environment.ProcessId.ToString()));
                _lease.Flush();
                _watcher = new FileSystemWatcher(_directory, "changed.signal")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    IncludeSubdirectories = false
                };
                _watcher.Changed += OnSignal;
                _watcher.Created += OnSignal;
                _watcher.Renamed += OnSignal;
                _watcher.Error += OnWatcherError;
                _watcherHealthy = true;
                _watcher.EnableRaisingEvents = true;
            }
            var result = await _photoshop.ExecuteScriptAsync("probe/document_events", Arguments(remove: false));
            if (!result.Success) throw result.Error ?? new IOException("无法注册 Photoshop 事件");
            using var receipt = JsonDocument.Parse(result.ReturnValue);
            var enabledByUs = receipt.RootElement.GetProperty("enabledByPSexp").GetBoolean();
            _hasRegistration = true;
            File.WriteAllText(Path.Combine(_directory, "enabled-by-psexp.state"), enabledByUs ? "1" : "0");
            lock (_state)
            {
                if (_enabled && !_disposed) _registeredPid = processId;
                else Deactivate();
            }
            if (_registeredPid == 0) await RemoveCoreAsync();
            else _logger.Info($"[document-events] subscribed pid={processId} registered={receipt.RootElement.GetProperty("registered")} rejected={receipt.RootElement.GetProperty("rejected")}；后台只接收变化标记，不读取文档。");
        }
        catch (Exception error)
        {
            lock (_state) Deactivate();
            _logger.Warn("[document-events] 接入失败，保留前台识别作为兜底：" + error);
        }
        finally { _operations.Release(); }
    }

    private Dictionary<string, string> Arguments(bool remove)
    {
        var restore = false;
        try { restore = File.ReadAllText(Path.Combine(_directory, "enabled-by-psexp.state")).Trim() == "1"; }
        catch (IOException) { }
        return new()
        {
            ["HANDLER_PATH"] = ScriptParameterInjector.EscapeJsString(Path.Combine(_directory, "notifier.jsx")),
            ["REMOVE_ONLY"] = remove ? "true" : "false",
            ["RESTORE_DISABLED"] = restore ? "true" : "false"
        };
    }

    private void OnSignal(object sender, FileSystemEventArgs args)
    {
        lock (_state)
        {
            if (_disposed || !_enabled || _lease is null) return;
            Interlocked.Increment(ref _version);
            // One delayed callback after a burst, never a periodic refresh timer.
            _debounce.Change(180, Timeout.Infinite);
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs args)
    {
        lock (_state) _watcherHealthy = false;
        _logger.Warn("[document-events] 系统通知丢失，下一次前台识别会校准：" + args.GetException());
        OnSignal(sender, new FileSystemEventArgs(WatcherChangeTypes.Changed, _directory, "changed.signal"));
    }

    private void Notify()
    {
        lock (_state) { if (_disposed || !_enabled || _lease is null) return; }
        _logger.Debug($"[document-events] changed version={Version}；仅通知界面检查前台资格。");
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception error) { _logger.Debug("[document-events] 接收窗口已退出：" + error.Message); }
    }

    // Called under _state. DeleteOnClose also removes the activation lease on a process crash.
    private void Deactivate()
    {
        _registeredPid = 0;
        _watcherHealthy = false;
        if (!_disposed) _debounce.Change(Timeout.Infinite, Timeout.Infinite);
        _watcher?.Dispose();
        _watcher = null;
        _lease?.Dispose();
        _lease = null;
    }

    private async Task RemoveCoreAsync()
    {
        if (!_hasRegistration) return;
        var result = await _photoshop.ExecuteScriptAsync("probe/document_events", Arguments(remove: true));
        if (!result.Success) throw result.Error ?? new IOException("无法清理 Photoshop 事件");
        _hasRegistration = false;
        File.WriteAllText(Path.Combine(_directory, "enabled-by-psexp.state"), "0");
        _logger.Info("[document-events] 已移除本应用的 Photoshop 事件；其他脚本配置保留。");
    }

    private async Task UnsubscribeAsync()
    {
        await _operations.WaitAsync();
        try
        {
            lock (_state) { if (_enabled && !_disposed) return; }
            await RemoveCoreAsync();
        }
        catch (Exception error) { _logger.Debug("[document-events] 事件清理未完成；租约已失效，遗留回调不再写入：" + error.Message); }
        finally { _operations.Release(); }
    }

    public Task StopAsync()
    {
        Dispose();
        return _cleanup;
    }

    public void Dispose()
    {
        lock (_state)
        {
            if (_disposed) return;
            _enabled = false;
            Deactivate();
            _disposed = true;
            _debounce.Dispose();
        }
        _cleanup = UnsubscribeAsync();
    }
}
