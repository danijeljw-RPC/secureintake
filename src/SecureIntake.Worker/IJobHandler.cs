using SecureIntake.Infrastructure.Jobs;

namespace SecureIntake.Worker;

/// <summary>Handles one job type. Handlers must be idempotent: delivery is at least once (F12).</summary>
public interface IJobHandler
{
    string JobType { get; }
    Task HandleAsync(ClaimedJob job, CancellationToken ct);
}

/// <summary>A failure whose message is safe to store and show to operators (no secrets, no tenant content).</summary>
public sealed class JobFailedException : Exception
{
    public JobFailedException(string safeMessage) : base(safeMessage) { }
    public JobFailedException() { }
    public JobFailedException(string message, Exception inner) : base(message, inner) { }
}
