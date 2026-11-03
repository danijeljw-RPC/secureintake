using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SecureIntake.Infrastructure.Jobs;

namespace SecureIntake.Worker;

public sealed record WorkerOptions(int Concurrency, TimeSpan Lease, TimeSpan IdleDelay);

/// <summary>
/// Claims and runs durable jobs with bounded concurrency. Leases are heartbeated while a handler runs,
/// so a crashed worker's jobs become claimable again once the lease expires.
/// </summary>
public sealed partial class JobDispatcher(JobStore jobs, IEnumerable<IJobHandler> handlers, WorkerOptions options, ILogger<JobDispatcher> log)
    : BackgroundService
{
    private readonly Dictionary<string, IJobHandler> _handlers = handlers.ToDictionary(h => h.JobType, StringComparer.Ordinal);
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}"[..48];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(log, _workerId, options.Concurrency);
        var slots = new SemaphoreSlim(options.Concurrency, options.Concurrency);
        var running = new List<Task>();
        while (!stoppingToken.IsCancellationRequested)
        {
            await slots.WaitAsync(stoppingToken).ConfigureAwait(false);
            ClaimedJob? job;
            try
            {
                job = await jobs.ClaimAsync(_workerId, options.Lease, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                slots.Release();
                LogClaimFailed(log, ex);
                await Task.Delay(options.IdleDelay, stoppingToken).ConfigureAwait(false);
                continue;
            }
            if (job is null)
            {
                slots.Release();
                await Task.Delay(options.IdleDelay, stoppingToken).ConfigureAwait(false);
                continue;
            }
            running.RemoveAll(t => t.IsCompleted);
            running.Add(RunAsync(job, slots, stoppingToken));
        }
        await Task.WhenAll(running).ConfigureAwait(false);
    }

    /// <summary>Runs one claimed job. Exposed for tests and the host's <c>jobs run-once</c> verb.</summary>
    public async Task RunOnceAsync(ClaimedJob job, CancellationToken ct)
    {
        if (!_handlers.TryGetValue(job.Type, out var handler))
        {
            await jobs.FailAsync(job, _workerId, $"No handler for job type '{job.Type}'.", ct).ConfigureAwait(false);
            return;
        }
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = HeartbeatAsync(job, heartbeatCts.Token);
        try
        {
            await handler.HandleAsync(job, ct).ConfigureAwait(false);
            await jobs.CompleteAsync(job.Id, _workerId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only messages explicitly marked safe are stored; everything else is recorded by type.
            var safe = ex is JobFailedException ? ex.Message : ex.GetType().Name;
            LogJobFailed(log, job.Id, job.Type, safe);
            await jobs.FailAsync(job, _workerId, safe, ct).ConfigureAwait(false);
        }
        finally
        {
            await heartbeatCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await heartbeat.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public string WorkerId => _workerId;

    private async Task RunAsync(ClaimedJob job, SemaphoreSlim slots, CancellationToken ct)
    {
        try
        {
            await RunOnceAsync(job, ct).ConfigureAwait(false);
        }
        finally
        {
            slots.Release();
        }
    }

    private async Task HeartbeatAsync(ClaimedJob job, CancellationToken ct)
    {
        var interval = options.Lease / 3;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(interval, ct).ConfigureAwait(false);
            await jobs.HeartbeatAsync(job.Id, _workerId, options.Lease, ct).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Job dispatcher {WorkerId} started with concurrency {Concurrency}")]
    private static partial void LogStarted(ILogger logger, string workerId, int concurrency);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job claim failed")]
    private static partial void LogClaimFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId} ({JobType}) failed: {Reason}")]
    private static partial void LogJobFailed(ILogger logger, Guid jobId, string jobType, string reason);
}
