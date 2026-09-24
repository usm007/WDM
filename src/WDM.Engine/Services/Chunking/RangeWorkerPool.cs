using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WDM.Services.Chunking;

/// <summary>Shared run state: first failure wins and cancels the run fast.</summary>
internal sealed class WorkerRunContext : IDisposable
{
    private readonly CancellationTokenSource _linked;
    private readonly CancellationTokenSource _failCts = new();
    private Exception? _failure;
    private readonly object _lock = new();
    private bool _disposed;

    public CancellationToken Token => _linked.Token;

    public WorkerRunContext(CancellationToken sessionToken)
    {
        _linked = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, _failCts.Token);
    }

    public void Fail(Exception ex)
    {
        lock (_lock) _failure ??= ex;
        try { _failCts.Cancel(); } catch { }
    }

    public Exception? Failure
    {
        get { lock (_lock) return _failure; }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { _linked.Dispose(); } catch { }
        try { _failCts.Dispose(); } catch { }
    }
}

/// <summary>
/// Fixed pool of workers; the scheduler's concurrency gate (active &lt; desired)
/// parks extras without task churn. Workers own no state across leases.
/// </summary>
internal static class RangeWorkerPool
{
    public static Task RunWorkerAsync(
        int workerId,
        RangeScheduler scheduler,
        CompletionMap map,
        MirrorSelector mirrors,
        SchedulerController controller,
        DownloadTelemetry telemetry,
        RangeTransportDeps deps,
        WorkerRunContext ctx)
    {
        return Task.Run(async () =>
        {
            try
            {
                while (!ctx.Token.IsCancellationRequested)
                {
                    long desired = controller.DesiredRangeSize();
                    var lease = scheduler.TryLease(workerId, desired);
                    if (lease is null)
                    {
                        if (!scheduler.HasWork)
                            return;
                        await Task.Delay(100, ctx.Token);
                        continue;
                    }

                    telemetry.SetActiveWorkers(scheduler.ActiveCount);
                    try
                    {
                        await RangeTransport.FetchRangeAsync(
                            lease, lease.Generation, scheduler, map, mirrors,
                            controller, telemetry, deps, ctx.Token);
                        scheduler.Complete(lease.Id, lease.Generation);
                    }
                    catch (LeaseInvalidatedException)
                    {
                        // Split under us: committed bytes are safe and both halves
                        // are already pending. Take a new lease, never requeue.
                        continue;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        // Remainder (if any) goes back to pending; budget-exhausted
                        // or fatal errors fail the whole run.
                        scheduler.Abort(lease.Id, lease.Generation);
                        ctx.Fail(ex);
                        return;
                    }
                    finally
                    {
                        telemetry.SetActiveWorkers(scheduler.ActiveCount);
                    }
                }
            }
            catch (OperationCanceledException) { /* run teardown */ }
        }, CancellationToken.None);
    }
}
