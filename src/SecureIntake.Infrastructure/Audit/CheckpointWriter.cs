using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using SecureIntake.Cryptography.Audit;
using SecureIntake.Cryptography.Signing;
using SecureIntake.Infrastructure.Data;

namespace SecureIntake.Infrastructure.Audit;

/// <summary>External checkpoint sink (F11). Restores are detected against it (doc 18).</summary>
public interface ICheckpointSink
{
    Task WriteAsync(AuditCheckpoint checkpoint, byte[] signature, CancellationToken ct);
}

/// <summary>Appends one JSON line per checkpoint. Point it at storage the application host can't rewrite.</summary>
public sealed class FileCheckpointSink(string path) : ICheckpointSink, IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public void Dispose() => _lock.Dispose();

    public async Task WriteAsync(AuditCheckpoint checkpoint, byte[] signature, CancellationToken ct)
    {
        var c = checkpoint;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("format", AuditCheckpoint.Format);
            w.WriteString("scope", c.Scope);
            w.WriteNumber("seq", c.Seq);
            w.WriteString("hash", Base64Url.EncodeToString(c.Hash));
            w.WriteNumber("recoveryEpoch", c.RecoveryEpoch);
            w.WriteNumber("time", c.Time);
            w.WriteString("keyId", c.KeyId);
            w.WriteString("signature", Base64Url.EncodeToString(signature));
            w.WriteEndObject();
        }
        var line = Encoding.UTF8.GetString(ms.ToArray()) + "\n";
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await File.AppendAllTextAsync(path, line, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>
/// Signs the head of every chain that has advanced since its last checkpoint. Runs as an essential job
/// every 1,000 records or 15 minutes (doc 27). The checkpoint is stored, then copied to the sink; if the
/// sink fails the job fails and retries, and readiness reports the sink as degraded.
/// </summary>
public sealed class CheckpointWriter(Database database, Es256Signer signer, ICheckpointSink sink, TimeProvider time)
{
    public async Task<int> CheckpointAllAsync(CancellationToken ct = default)
    {
        var written = 0;
        var epoch = await RecoveryEpochAsync(ct).ConfigureAwait(false);
        if (await CheckpointScopeAsync(AuditRecordWriter.PlatformScope, null, epoch, ct).ConfigureAwait(false))
        {
            written++;
        }
        foreach (var org in await OrganisationIdsAsync(ct).ConfigureAwait(false))
        {
            if (await CheckpointScopeAsync(AuditRecordWriter.OrganisationScope(org), org, epoch, ct).ConfigureAwait(false))
            {
                written++;
            }
        }
        return written;
    }

    private async Task<bool> CheckpointScopeAsync(string scope, Guid? org, long epoch, CancellationToken ct)
    {
        var d = database.Dialect;
        var result = await database.InTransactionAsync<(AuditCheckpoint Checkpoint, byte[] Signature)?>(async tx =>
        {
            if (org is { } o)
            {
                await d.SetTenantContextAsync(tx.Connection!, tx, o).ConfigureAwait(false);
            }
            long seq;
            byte[] hash;
            await using (var head = org is { } oid
                ? tx.Command("SELECT seq, hash FROM organisation_audit_heads WHERE organisation_id = @k").Param("k", d.Id(oid))
                : tx.Command("SELECT seq, hash FROM audit_heads WHERE scope = @k").Param("k", scope))
            {
                await using var r = await head.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    return null;
                }
                (seq, hash) = (r.GetInt64(0), r.GetBytes(1));
            }
            if (seq == 0)
            {
                return null;
            }
            await using (var last = tx.Command("SELECT MAX(seq) FROM audit_checkpoints WHERE scope = @s").Param("s", scope))
            {
                var max = await last.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (max is not null and not DBNull && Convert.ToInt64(max, System.Globalization.CultureInfo.InvariantCulture) >= seq)
                {
                    return null;
                }
            }
            var checkpoint = new AuditCheckpoint(scope, seq, hash, epoch, time.GetUtcNow().ToUnixTimeSeconds(), signer.KeyId);
            var signature = checkpoint.Sign(signer);
            await using (var ins = tx.Command("""
                INSERT INTO audit_checkpoints (scope, seq, hash, recovery_epoch, time, key_id, signature)
                VALUES (@s, @q, @h, @e, @t, @k, @sig)
                """)
                .Param("s", scope).Param("q", seq).Param("h", hash).Param("e", epoch).Param("t", checkpoint.Time)
                .Param("k", checkpoint.KeyId).Param("sig", signature))
            {
                await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            return (checkpoint, signature);
        }, ct).ConfigureAwait(false);

        if (result is not { } written)
        {
            return false;
        }
        await sink.WriteAsync(written.Checkpoint, written.Signature, ct).ConfigureAwait(false);
        return true;
    }

    private Task<long> RecoveryEpochAsync(CancellationToken ct) =>
        database.InTransactionAsync(async tx =>
        {
            await using var cmd = tx.Command("SELECT value FROM deployment_state WHERE key = 'recovery_epoch'");
            return long.Parse((string)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!, System.Globalization.CultureInfo.InvariantCulture);
        }, ct);

    private Task<List<Guid>> OrganisationIdsAsync(CancellationToken ct) =>
        database.InTransactionAsync(async tx =>
        {
            var d = database.Dialect;
            await using var cmd = tx.Command("SELECT id FROM organisations ORDER BY id");
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var ids = new List<Guid>();
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                ids.Add(d.ReadId(r, 0));
            }
            return ids;
        }, ct);
}
