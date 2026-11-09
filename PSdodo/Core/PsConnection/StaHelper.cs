using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace PsExporter.Core.PsConnection;

/// <summary>
/// 在持久后台 STA 线程上执行 COM 与 Shell 调用（避免每次探测/导出都新建与销毁 OS 线程）
/// </summary>
internal static class StaHelper
{
    private static readonly BlockingCollection<Action> WorkQueue = new();
    private static readonly Thread WorkerThread = CreateWorkerThread();

    private static Thread CreateWorkerThread()
    {
        var thread = new Thread(() =>
        {
            foreach (var action in WorkQueue.GetConsumingEnumerable())
                action();
        })
        {
            Name = "PSexp-STA-Worker",
            IsBackground = true
        };
        if (OperatingSystem.IsWindows())
            thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    }

    public static T Run<T>(Func<T> func)
    {
        if (Thread.CurrentThread == WorkerThread || Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return func();

        return RunAsync(func).GetAwaiter().GetResult();
    }

    public static Task<T> RunAsync<T>(Func<T> func, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
            return Task.FromCanceled<T>(ct);

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkQueue.Add(() =>
        {
            if (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
                return;
            }
            try
            {
                tcs.TrySetResult(func());
            }
            catch (OperationCanceledException oce)
            {
                tcs.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }
}
