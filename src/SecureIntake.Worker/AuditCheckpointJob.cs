using Microsoft.Extensions.Hosting;
using SecureIntake.Infrastructure.Audit;
using SecureIntake.Infrastructure.Jobs;

namespace SecureIntake.Worker;

/// <summary>Signs advanced audit chain heads and copies the checkpoints to the external sink.</summary>
public sealed class AuditCheckpointJob(CheckpointWriter writer) : IJobHandler
{
    public const string Type = "audit.checkpoint@1";
    public string JobType => Type;

    public async Task HandleAsync(ClaimedJob job, CancellationToken ct)
    {
        try
        {
            await writer.CheckpointAllAsync(ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            throw new JobFailedException("Checkpoint sink write failed.");
        }
    }
}

/// <summary>
/// Enqueues an essential checkpoint job on a fixed interval. The idempotency key is the interval bucket,
/// so several worker processes produce one job per interval. (The 1,000-record trigger is added with the
/// audit write path's post-commit hook.)
/// </summary>
public sealed class AuditCheckpointScheduler(JobStore jobs, TimeProvider time, TimeSpan interval) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval, time);
        try
        {
            do
            {
                var bucket = time.GetUtcNow().ToUnixTimeSeconds() / (long)interval.TotalSeconds;
                await jobs.EnqueueAsync(new JobRequest(AuditCheckpointJob.Type, "{}", PriorityClass: PriorityClass.Essential,
                    IdempotencyKey: $"audit.checkpoint:{bucket}"), stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown, including a host that stops because it failed to start.
            // Not an error: the host would otherwise log it as a crashed background service.
        }
    }
}
