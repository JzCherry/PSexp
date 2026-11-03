using System;
using System.Threading;
using Avalonia;
using PsExporter.Core.Automation;
using PsExporter.Core.PsConnection;
using PsExporter.Core.Scripting;
using PsExporter.Services;

namespace PsExporter;

class Program
{
    private const string InstanceName = @"Local\PSexpV3";
    internal static string ExportPipeName
    {
        get
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return "PSexpV3.Export." + process.SessionId;
        }
    }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("PSexpComProbe"), "1", StringComparison.Ordinal))
        {
            Environment.ExitCode = RunComProbe();
            return;
        }

        // Match normal Photoshop permissions before creating the singleton or any window/animation state.
        if (WindowsProcessPermissions.TryRelaunchForPhotoshop(args)) return;
        args = Array.FindAll(args, argument => argument != WindowsProcessPermissions.RelaunchArgument);
        var silentStartup = args is [WindowsStartup.Argument];
        if (silentStartup) args = [];

        if (!ExportInvocation.TryParse(args, out var invocation))
        {
            Environment.ExitCode = 2;
            return;
        }

        using var singleInstance = new SingleInstanceCoordinator(InstanceName);
        if (!singleInstance.IsPrimary)
        {
            if (silentStartup) return;
            if (invocation is null) singleInstance.SignalPrimary();
            else if (!ExportCommandPipe.SendAsync(ExportPipeName, args).GetAwaiter().GetResult())
                Environment.ExitCode = 3;
            return;
        }

        App.SingleInstance = singleInstance;
        App.StartupExport = invocation;
        App.SilentStartup = silentStartup;
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
        }
        finally
        {
            App.SingleInstance = null;
        }
    }

    private static int RunComProbe()
    {
        try
        {
            var bridge = new WindowsComBridge();
            var script = ScriptLibrary.GetScript(ScriptNames.ProbeDocumentInfo);
            var result = bridge.Execute(script);
            Console.WriteLine(result.ReturnValue);
            return result.Success ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

public sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex _instanceMutex;
    private readonly EventWaitHandle _activationEvent;
    private readonly ManualResetEvent _shutdownEvent = new(false);
    private Thread? _listenerThread;
    private int _isListening;
    private int _isDisposed;

    public SingleInstanceCoordinator(string instanceName)
    {
        if (string.IsNullOrWhiteSpace(instanceName))
            throw new ArgumentException("单实例名称不能为空", nameof(instanceName));

        _instanceMutex = new Mutex(false, instanceName + ".Mutex", out var createdNew);
        _activationEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            instanceName + ".Activate");
        IsPrimary = createdNew;
    }

    public bool IsPrimary { get; }

    public void SignalPrimary()
    {
        ObjectDisposedException.ThrowIf(_isDisposed != 0, this);
        if (!IsPrimary)
            _activationEvent.Set();
    }

    public void StartListening(Action activationRequested)
    {
        ArgumentNullException.ThrowIfNull(activationRequested);
        ObjectDisposedException.ThrowIf(_isDisposed != 0, this);
        if (!IsPrimary || Interlocked.Exchange(ref _isListening, 1) != 0)
            return;

        _listenerThread = new Thread(() =>
        {
            var waitHandles = new WaitHandle[] { _activationEvent, _shutdownEvent };
            while (WaitHandle.WaitAny(waitHandles) == 0)
            {
                try
                {
                    activationRequested();
                }
                catch
                {
                    // 激活失败不应终止单实例监听，允许下次重复启动再次尝试。
                }
            }
        })
        {
            IsBackground = true,
            Name = "PSexp single-instance activation listener"
        };
        _listenerThread.Start();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            return;

        _shutdownEvent.Set();
        if (_listenerThread is { IsAlive: true } listener
            && listener != Thread.CurrentThread)
        {
            listener.Join(TimeSpan.FromSeconds(1));
        }

        _activationEvent.Dispose();
        _shutdownEvent.Dispose();
        _instanceMutex.Dispose();
    }
}
