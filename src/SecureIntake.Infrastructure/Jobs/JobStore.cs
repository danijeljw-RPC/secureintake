using System.Security.Cryptography;
using SecureIntake.Infrastructure.Data;

namespace SecureIntake.Infrastructure.Jobs;

public static class PriorityClass
{
    /// <summary>Operational work that reserved capacity protects (doc 27): checkpoints, fencing, cleanup.</summary>
    public const string Essential = "essential";
    public const string Interactive = "interactive";
    public const string Background = "background";
}

public sealed record JobRequest(string Type, string PayloadJson, Guid? OrganisationId = null, string PriorityClass = Jobs.PriorityClass.Background,
    int PayloadVersion = 1, int MaxAttempts = 8, string? IdempotencyKey = null, long? Generation = null, DateTimeOffset? NotBefore = null);

public sealed record ClaimedJob(Guid Id, Guid? OrganisationId, string Type, int PayloadVersion, string PayloadJson, int Attempts, int MaxAttempts, long? Generation);

/// <summary>
/// Durable jobs (F12). PostgreSQL claims with <c>FOR UPDATE SKIP LOCKED</c>; SQLite relies on IMMEDIATE
/// transactions and a single in-process dispatcher. Expired leases are re-claimable.
/// Ordering is strict priority class, then due time. The DRR fair scheduler across organisations
/// (doc 04 §9) replaces this ordering in M1's second half.
/// </summary>
public sealed class JobStore(Database database, TimeProvider time)
{
    public Task<Guid> EnqueueAsync(JobRequest job, CancellationToken ct = default)
    {
        var d = database.Dialect;
        return database.InTransactionAsync(async tx =>
        {
            if (job.IdempotencyKey is not null)
            {
                await using var existing = tx.Command("SELECT id FROM jobs WHERE idempotency_key = @k").Param("k", job.IdempotencyKey);
                await using var r = await existing.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    return d.ReadId(r, 0);
                }
            }
            var id = Guid.CreateVersion7(time.GetUtcNow());
            var now = time.GetUtcNow().ToUnixTimeMilliseconds();
            await using var cmd = tx.Command("""
                INSERT INTO jobs (id, organisation_id, class, type, payload_version, payload, state, priority_class, attempts,
                                  max_attempts, not_before, idempotency_key, generation, created_at, updated_at)
                VALUES (@id, @org, @class, @type, @pv, @payload, 'pending', @class, 0, @max, @nb, @ik, @gen, @now, @now)
                """)
                .Param("id", d.Id(id)).Param("org", d.Id(job.OrganisationId)).Param("class", job.PriorityClass).Param("type", job.Type)
                .Param("pv", job.PayloadVersion).Param("payload", job.PayloadJson).Param("max", job.MaxAttempts)
                .Param("nb", (job.NotBefore ?? time.GetUtcNow()).ToUnixTimeMilliseconds()).Param("ik", job.IdempotencyKey)
                .Param("gen", job.Generation).Param("now", now);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return id;
        }, ct);
    }

    public Task<ClaimedJob?> ClaimAsync(string workerId, TimeSpan lease, CancellationToken ct = default)
    {
        var d = database.Dialect;
        return database.InTransactionAsync(async tx =>
        {
            var now = time.GetUtcNow().ToUnixTimeMilliseconds();
            await using var cmd = tx.Command($"""
                UPDATE jobs SET state = 'running', lease_owner = @w, lease_expires_at = @exp, attempts = attempts + 1, updated_at = @now
                WHERE id = (
                    SELECT id FROM jobs
                    WHERE (state = 'pending' AND not_before <= @now) OR (state = 'running' AND lease_expires_at < @now)
                    ORDER BY CASE priority_class WHEN 'essential' THEN 0 WHEN 'interactive' THEN 1 ELSE 2 END, not_before
                    LIMIT 1{d.ForUpdateSkipLocked})
                RETURNING id, organisation_id, type, payload_version, payload, attempts, max_attempts, generation
                """)
                .Param("w", workerId).Param("exp", now + (long)lease.TotalMilliseconds).Param("now", now);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await r.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }
            return new ClaimedJob(d.ReadId(r, 0), d.ReadNullableId(r, 1), r.GetString(2), r.GetInt32(3), r.GetString(4),
                r.GetInt32(5), r.GetInt32(6), r.GetNullableInt64(7));
        }, ct);
    }

    public Task<bool> HeartbeatAsync(Guid jobId, string workerId, TimeSpan lease, CancellationToken ct = default) =>
        Update("UPDATE jobs SET lease_expires_at = @exp, updated_at = @now WHERE id = @id AND lease_owner = @w AND state = 'running'",
            jobId, workerId, null, lease, ct);

    public Task<bool> CompleteAsync(Guid jobId, string workerId, CancellationToken ct = default) =>
        Update("UPDATE jobs SET state = 'succeeded', lease_owner = NULL, lease_expires_at = NULL, updated_at = @now WHERE id = @id AND lease_owner = @w AND state = 'running'",
            jobId, workerId, null, null, ct);

    /// <summary>Retries with exponential backoff and jitter, or moves the job to <c>dead</c> after its last attempt.</summary>
    public Task<bool> FailAsync(ClaimedJob job, string workerId, string safeError, CancellationToken ct = default)
    {
        var dead = job.Attempts >= job.MaxAttempts;
        var baseDelay = Math.Min(3600, 2 * Math.Pow(2, job.Attempts));
        var delayMs = (long)(baseDelay * 1000 * (0.5 + (RandomNumberGenerator.GetInt32(1000) / 1000.0)));
        var sql = dead
            ? "UPDATE jobs SET state = 'dead', last_error = @err, lease_owner = NULL, lease_expires_at = NULL, updated_at = @now WHERE id = @id AND lease_owner = @w"
            : "UPDATE jobs SET state = 'pending', last_error = @err, lease_owner = NULL, lease_expires_at = NULL, not_before = @now + @delay, updated_at = @now WHERE id = @id AND lease_owner = @w";
        return Update(sql, job.Id, workerId, cmd => cmd.Param("err", Truncate(safeError)).Param("delay", delayMs), null, ct);
    }

    /// <summary>Manual replay of a dead job. Needs <c>platform.operations.jobs.manage</c> at the API.</summary>
    public Task<bool> ReplayDeadAsync(Guid jobId, CancellationToken ct = default) =>
        Update("UPDATE jobs SET state = 'pending', attempts = 0, not_before = @now, updated_at = @now WHERE id = @id AND state = 'dead'",
            jobId, null, null, null, ct);

    public Task<Dictionary<string, long>> CountsByStateAsync(CancellationToken ct = default) =>
        database.InTransactionAsync(async tx =>
        {
            await using var cmd = tx.Command("SELECT state, COUNT(*) FROM jobs GROUP BY state");
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                counts[r.GetString(0)] = r.GetInt64(1);
            }
            return counts;
        }, ct);

    private Task<bool> Update(string sql, Guid id, string? worker, Action<System.Data.Common.DbCommand>? extra, TimeSpan? lease, CancellationToken ct)
    {
        var d = database.Dialect;
        return database.InTransactionAsync(async tx =>
        {
            var now = time.GetUtcNow().ToUnixTimeMilliseconds();
            await using var cmd = tx.Command(sql).Param("id", d.Id(id)).Param("now", now);
            if (worker is not null)
            {
                cmd.Param("w", worker);
            }
            if (lease is { } l)
            {
                cmd.Param("exp", now + (long)l.TotalMilliseconds);
            }
            extra?.Invoke(cmd);
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }, ct);
    }

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500];
}
