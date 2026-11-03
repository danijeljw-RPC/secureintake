using System.Text;
using SecureIntake.Cryptography.Audit;
using SecureIntake.Cryptography.Signing;
using SecureIntake.Infrastructure.Data;

namespace SecureIntake.Infrastructure.Audit;

public sealed record AuditVerificationResult(string Scope, long Records, long? FirstBadSeq, int CheckpointsVerified, int CheckpointsFailed, string? Error)
{
    public bool IsValid => FirstBadSeq is null && CheckpointsFailed == 0 && Error is null;
}

/// <summary>Recomputes a chain from storage and checks every stored checkpoint against it.</summary>
public sealed class AuditVerifier(Database database)
{
    public async Task<AuditVerificationResult> VerifyPlatformAsync(Func<string, Es256Verifier?> verifierForKey, CancellationToken ct = default) =>
        await VerifyAsync(AuditRecordWriter.PlatformScope,
            "SELECT seq, record_json, prev_hash, hash FROM platform_audit ORDER BY seq", null, verifierForKey, ct).ConfigureAwait(false);

    /// <summary>
    /// Verifies one organisation's chain. Runs inside a tenant-bound transaction, so on PostgreSQL RLS still applies.
    /// </summary>
    public async Task<AuditVerificationResult> VerifyOrganisationAsync(Guid organisationId, Func<string, Es256Verifier?> verifierForKey, CancellationToken ct = default) =>
        await VerifyAsync(AuditRecordWriter.OrganisationScope(organisationId),
            "SELECT seq, record_json, prev_hash, hash FROM organisation_audit WHERE organisation_id = @o ORDER BY seq", organisationId, verifierForKey, ct).ConfigureAwait(false);

    private async Task<AuditVerificationResult> VerifyAsync(string scope, string sql, Guid? org, Func<string, Es256Verifier?> verifierForKey, CancellationToken ct)
    {
        var d = database.Dialect;
        return await database.InTransactionAsync(async tx =>
        {
            if (org is { } o)
            {
                await d.SetTenantContextAsync(tx.Connection!, tx, o).ConfigureAwait(false);
            }
            var hashesBySeq = new Dictionary<long, byte[]>();
            var prev = AuditChain.Genesis;
            long expectedSeq = 1, count = 0;
            long? bad = null;
            await using (var cmd = tx.Command(sql))
            {
                if (org is { } oid)
                {
                    cmd.Param("o", d.Id(oid));
                }
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    var seq = r.GetInt64(0);
                    var json = Encoding.UTF8.GetBytes(r.GetString(1));
                    var storedPrev = r.GetBytes(2);
                    var storedHash = r.GetBytes(3);
                    count++;
                    if (bad is null)
                    {
                        if (seq != expectedSeq || AuditChain.Verify(prev, [(json, storedPrev, storedHash)]) != -1
                            || !RecordClaims(json, scope, seq))
                        {
                            bad = seq;
                        }
                        else
                        {
                            prev = storedHash;
                            hashesBySeq[seq] = storedHash;
                        }
                    }
                    expectedSeq = seq + 1;
                }
            }

            int ok = 0, failed = 0;
            await using (var cp = tx.Command("SELECT seq, hash, recovery_epoch, time, key_id, signature FROM audit_checkpoints WHERE scope = @s ORDER BY seq").Param("s", scope))
            {
                await using var r = await cp.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    var checkpoint = new AuditCheckpoint(scope, r.GetInt64(0), r.GetBytes(1), r.GetInt64(2), r.GetInt64(3), r.GetString(4));
                    var verifier = verifierForKey(checkpoint.KeyId);
                    var matches = hashesBySeq.TryGetValue(checkpoint.Seq, out var h) && h.AsSpan().SequenceEqual(checkpoint.Hash);
                    if (verifier is not null && matches && checkpoint.Verify(r.GetBytes(5), verifier))
                    {
                        ok++;
                    }
                    else
                    {
                        failed++;
                    }
                }
            }
            return new AuditVerificationResult(scope, count, bad, ok, failed, null);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>The record must name its own scope and seq, so rows can't be replayed into another chain or position.</summary>
    private static bool RecordClaims(byte[] json, string scope, long seq)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        return root.TryGetProperty("scope", out var s) && s.GetString() == scope
            && root.TryGetProperty("seq", out var q) && q.TryGetInt64(out var n) && n == seq;
    }
}
