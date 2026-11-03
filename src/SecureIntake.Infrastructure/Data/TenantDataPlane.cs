using System.Data.Common;
using SecureIntake.Application.Abstractions;
using SecureIntake.Domain.Intake;
using SecureIntake.Infrastructure.Audit;

namespace SecureIntake.Infrastructure.Data;

/// <summary>
/// Shared-tier tenant data plane. Every query is filtered by <c>organisation_id</c> in SQL, and on PostgreSQL
/// RLS enforces the same boundary as defence in depth. The routing assignment is read in the same
/// transaction, and writes fail if it's fenced (doc 03 §8).
/// </summary>
public sealed class TenantDataPlane(Database database, TimeProvider time) : ITenantDataPlane
{
    public Task<T> ExecuteAsync<T>(Guid organisationId, Func<ITenantTransaction, Task<T>> work, CancellationToken ct = default)
    {
        if (organisationId == Guid.Empty)
        {
            throw new ArgumentException("A tenant transaction needs an organisation.", nameof(organisationId));
        }
        var d = database.Dialect;
        return database.InTransactionAsync(async tx =>
        {
            long generation;
            bool blocked;
            await using (var cmd = tx.Command($"SELECT assignment_generation, writes_blocked FROM organisation_resource_assignments WHERE organisation_id = @o{d.ForUpdate.Replace("UPDATE", "SHARE", StringComparison.Ordinal)}")
                .Param("o", d.Id(organisationId)))
            {
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    throw new TenantFencedException("Organisation has no resource assignment.");
                }
                generation = r.GetInt64(0);
                blocked = r.GetInt32(1) != 0;
            }
            await d.SetTenantContextAsync(tx.Connection!, tx, organisationId).ConfigureAwait(false);
            return await work(new Tx(tx, d, organisationId, generation, blocked, time)).ConfigureAwait(false);
        }, ct);
    }

    private sealed class Tx(DbTransaction tx, SqlDialect d, Guid organisationId, long generation, bool writesBlocked, TimeProvider time) : ITenantTransaction
    {
        public Guid OrganisationId => organisationId;
        public long Generation => generation;

        public async Task<Dropbox?> FindDropboxAsync(Guid id)
        {
            await using var cmd = tx.Command($"SELECT {Rows.DropboxColumns} FROM dropboxes WHERE organisation_id = @o AND id = @id")
                .Param("o", d.Id(organisationId)).Param("id", d.Id(id));
            await using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            return await r.ReadAsync().ConfigureAwait(false) ? Rows.ReadDropbox(r, d) : null;
        }

        public async Task<IReadOnlyList<Dropbox>> ListDropboxesAsync(int limit)
        {
            await using var cmd = tx.Command($"SELECT {Rows.DropboxColumns} FROM dropboxes WHERE organisation_id = @o ORDER BY id LIMIT @n")
                .Param("o", d.Id(organisationId)).Param("n", limit);
            await using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            var list = new List<Dropbox>();
            while (await r.ReadAsync().ConfigureAwait(false))
            {
                list.Add(Rows.ReadDropbox(r, d));
            }
            return list;
        }

        public async Task InsertDropboxAsync(Dropbox x)
        {
            EnsureWritable(x.OrganisationId);
            var now = time.GetUtcNow().ToUnixTimeMilliseconds();
            await using var cmd = tx.Command("""
                INSERT INTO dropboxes (id, organisation_id, name, status, version, created_at, updated_at)
                VALUES (@id, @o, @name, @status, @v, @t, @t)
                """)
                .Param("id", d.Id(x.Id)).Param("o", d.Id(organisationId)).Param("name", x.Name)
                .Param("status", x.Status.ToString()).Param("v", x.Version).Param("t", now);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        public async Task UpdateDropboxAsync(Dropbox x, long expectedVersion)
        {
            EnsureWritable(x.OrganisationId);
            await using var cmd = tx.Command("""
                UPDATE dropboxes SET status = @status, version = @v, updated_at = @t
                WHERE organisation_id = @o AND id = @id AND version = @expected
                """)
                .Param("status", x.Status.ToString()).Param("v", x.Version).Param("t", time.GetUtcNow().ToUnixTimeMilliseconds())
                .Param("o", d.Id(organisationId)).Param("id", d.Id(x.Id)).Param("expected", expectedVersion);
            if (await cmd.ExecuteNonQueryAsync().ConfigureAwait(false) != 1)
            {
                throw new ConcurrencyException();
            }
        }

        public Task AppendAuditAsync(AuditEntry entry)
        {
            EnsureWritable(organisationId);
            return AuditRecordWriter.AppendOrganisationAsync(tx, d, organisationId, entry, time);
        }

        public Task EnqueueOutboxAsync(OutboxEvent evt)
        {
            EnsureWritable(organisationId);
            if (evt.OrganisationId != organisationId)
            {
                throw new InvalidOperationException("Outbox event belongs to a different organisation.");
            }
            return Outbox.EnqueueAsync(tx, d, evt, time);
        }

        private void EnsureWritable(Guid entityOrganisation)
        {
            if (entityOrganisation != organisationId)
            {
                throw new InvalidOperationException("Entity belongs to a different organisation than this transaction.");
            }
            if (writesBlocked)
            {
                throw new TenantFencedException();
            }
        }
    }
}
