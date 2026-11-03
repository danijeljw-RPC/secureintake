using System.Data.Common;
using System.Text;
using System.Text.Json;
using SecureIntake.Application.Abstractions;
using SecureIntake.Cryptography.Audit;
using SecureIntake.Infrastructure.Data;

namespace SecureIntake.Infrastructure.Audit;

/// <summary>
/// Appends audit records to a chain inside the caller's transaction (F11). The chain head row is locked
/// (PostgreSQL) or writers are serialised (SQLite IMMEDIATE), so sequence numbers are gap-free.
/// </summary>
internal static class AuditRecordWriter
{
    public const string PlatformScope = "platform";

    public static string OrganisationScope(Guid organisationId) => "org:" + organisationId.ToString("D");

    public static async Task<long> AppendPlatformAsync(DbTransaction tx, SqlDialect d, AuditEntry entry, TimeProvider time)
    {
        entry.EnsureValid();
        await using (var ins = tx.Command("INSERT INTO audit_heads (scope, seq, hash) VALUES (@s, 0, @h) ON CONFLICT (scope) DO NOTHING")
            .Param("s", PlatformScope).Param("h", AuditChain.Genesis))
        {
            await ins.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        var (seq, prev) = await ReadHeadAsync(tx, $"SELECT seq, hash FROM audit_heads WHERE scope = @k{d.ForUpdate}", PlatformScope).ConfigureAwait(false);
        var next = seq + 1;
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var json = BuildRecordJson(PlatformScope, next, now, entry);
        var hash = AuditChain.Link(prev, json);

        await using (var cmd = tx.Command("INSERT INTO platform_audit (seq, time, event, record_json, prev_hash, hash) VALUES (@q, @t, @e, @j, @p, @h)")
            .Param("q", next).Param("t", now).Param("e", entry.Event).Param("j", Encoding.UTF8.GetString(json)).Param("p", prev).Param("h", hash))
        {
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        await using (var upd = tx.Command("UPDATE audit_heads SET seq = @q, hash = @h WHERE scope = @s")
            .Param("q", next).Param("h", hash).Param("s", PlatformScope))
        {
            await upd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        return next;
    }

    public static async Task<long> AppendOrganisationAsync(DbTransaction tx, SqlDialect d, Guid organisationId, AuditEntry entry, TimeProvider time)
    {
        entry.EnsureValid();
        var org = d.Id(organisationId);
        await using (var ins = tx.Command("INSERT INTO organisation_audit_heads (organisation_id, seq, hash) VALUES (@k, 0, @h) ON CONFLICT (organisation_id) DO NOTHING")
            .Param("k", org).Param("h", AuditChain.Genesis))
        {
            await ins.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        var (seq, prev) = await ReadHeadAsync(tx, $"SELECT seq, hash FROM organisation_audit_heads WHERE organisation_id = @k{d.ForUpdate}", org).ConfigureAwait(false);
        var next = seq + 1;
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var json = BuildRecordJson(OrganisationScope(organisationId), next, now, entry);
        var hash = AuditChain.Link(prev, json);

        await using (var cmd = tx.Command("INSERT INTO organisation_audit (organisation_id, seq, time, event, record_json, prev_hash, hash) VALUES (@o, @q, @t, @e, @j, @p, @h)")
            .Param("o", org).Param("q", next).Param("t", now).Param("e", entry.Event).Param("j", Encoding.UTF8.GetString(json)).Param("p", prev).Param("h", hash))
        {
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        await using (var upd = tx.Command("UPDATE organisation_audit_heads SET seq = @q, hash = @h WHERE organisation_id = @o")
            .Param("q", next).Param("h", hash).Param("o", org))
        {
            await upd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        return next;
    }

    private static async Task<(long Seq, byte[] Hash)> ReadHeadAsync(DbTransaction tx, string sql, object key)
    {
        await using var cmd = tx.Command(sql).Param("k", key);
        await using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await r.ReadAsync().ConfigureAwait(false))
        {
            throw new InvalidOperationException("Audit chain head is missing.");
        }
        return (r.GetInt64(0), r.GetBytes(1));
    }

    /// <summary>Serialises a record. Field order doesn't matter: hashing canonicalises with JCS.</summary>
    internal static byte[] BuildRecordJson(string scope, long seq, long timeMs, AuditEntry entry)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("format", "secure-intake-audit-record@1");
            w.WriteString("scope", scope);
            w.WriteNumber("seq", seq);
            w.WriteNumber("time", timeMs);
            w.WriteString("event", entry.Event);
            w.WriteString("actor", entry.Actor);
            if (entry.Target is null)
            {
                w.WriteNull("target");
            }
            else
            {
                w.WriteString("target", entry.Target);
            }
            w.WriteStartObject("data");
            foreach (var (key, value) in entry.Data.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                switch (value)
                {
                    case null: w.WriteNull(key); break;
                    case string s: w.WriteString(key, s); break;
                    case bool b: w.WriteBoolean(key, b); break;
                    case int i: w.WriteNumber(key, i); break;
                    case long l: w.WriteNumber(key, l); break;
                    case Guid g: w.WriteString(key, g.ToString("D")); break;
                    default: throw new ArgumentException($"Audit data '{key}' has unsupported type {value.GetType().Name}.");
                }
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }
}
