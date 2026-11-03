using System.Data.Common;
using SecureIntake.Application.Abstractions;
using SecureIntake.Domain.Organisations;
using SecureIntake.Infrastructure.Audit;

namespace SecureIntake.Infrastructure.Data;

public sealed class ControlPlane(Database database, TimeProvider time) : IControlPlane
{
    public Task<T> ExecuteAsync<T>(Func<IControlPlaneTransaction, Task<T>> work, CancellationToken ct = default) =>
        database.InTransactionAsync(tx => work(new Tx(tx, database.Dialect, time)), ct);

    private sealed class Tx(DbTransaction tx, SqlDialect d, TimeProvider time) : IControlPlaneTransaction
    {
        public Task<Organisation?> FindOrganisationAsync(Guid id) =>
            SingleOrg($"SELECT {Rows.OrganisationColumns} FROM organisations WHERE id = @k", d.Id(id));

        public Task<Organisation?> FindOrganisationBySlugAsync(string slug) =>
            SingleOrg($"SELECT {Rows.OrganisationColumns} FROM organisations WHERE slug = @k", slug);

        public async Task<IReadOnlyList<Organisation>> ListOrganisationsAsync(int limit)
        {
            await using var cmd = tx.Command($"SELECT {Rows.OrganisationColumns} FROM organisations ORDER BY id LIMIT @n").Param("n", limit);
            await using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            var list = new List<Organisation>();
            while (await r.ReadAsync().ConfigureAwait(false))
            {
                list.Add(Rows.ReadOrganisation(r, d));
            }
            return list;
        }

        public async Task InsertOrganisationAsync(Organisation o)
        {
            var now = time.GetUtcNow().ToUnixTimeMilliseconds();
            await using var cmd = tx.Command("""
                INSERT INTO organisations (id, slug, display_name, status, method, version, created_at, updated_at)
                VALUES (@id, @slug, @name, @status, @method, @version, @created, @updated)
                """)
                .Param("id", d.Id(o.Id)).Param("slug", o.Slug).Param("name", o.DisplayName).Param("status", o.Status.ToString())
                .Param("method", o.Method.ToString()).Param("version", o.Version)
                .Param("created", o.CreatedAt.ToUnixTimeMilliseconds()).Param("updated", now);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        public async Task UpdateOrganisationAsync(Organisation o, long expectedVersion)
        {
            await using var cmd = tx.Command("""
                UPDATE organisations SET display_name = @name, status = @status, version = @version, updated_at = @updated
                WHERE id = @id AND version = @expected
                """)
                .Param("name", o.DisplayName).Param("status", o.Status.ToString()).Param("version", o.Version)
                .Param("updated", time.GetUtcNow().ToUnixTimeMilliseconds()).Param("id", d.Id(o.Id)).Param("expected", expectedVersion);
            if (await cmd.ExecuteNonQueryAsync().ConfigureAwait(false) != 1)
            {
                throw new ConcurrencyException();
            }
        }

        public async Task<ResourceAssignment?> FindAssignmentAsync(Guid organisationId)
        {
            await using var cmd = tx.Command("""
                SELECT organisation_id, database_profile, storage_profile, assignment_generation, writes_blocked
                FROM organisation_resource_assignments WHERE organisation_id = @k
                """).Param("k", d.Id(organisationId));
            await using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            return await r.ReadAsync().ConfigureAwait(false)
                ? new ResourceAssignment(d.ReadId(r, 0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt32(4) != 0)
                : null;
        }

        public async Task UpsertAssignmentAsync(ResourceAssignment a)
        {
            await using var cmd = tx.Command("""
                INSERT INTO organisation_resource_assignments (organisation_id, database_profile, storage_profile, assignment_generation, writes_blocked, updated_at)
                VALUES (@o, @db, @st, @g, @w, @t)
                ON CONFLICT (organisation_id) DO UPDATE SET database_profile = excluded.database_profile,
                    storage_profile = excluded.storage_profile, assignment_generation = excluded.assignment_generation,
                    writes_blocked = excluded.writes_blocked, updated_at = excluded.updated_at
                """)
                .Param("o", d.Id(a.OrganisationId)).Param("db", a.DatabaseProfile).Param("st", a.StorageProfile)
                .Param("g", a.Generation).Param("w", a.WritesBlocked ? 1 : 0).Param("t", time.GetUtcNow().ToUnixTimeMilliseconds());
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        public Task AppendAuditAsync(AuditEntry entry) => AuditRecordWriter.AppendPlatformAsync(tx, d, entry, time);

        public Task EnqueueOutboxAsync(OutboxEvent evt) => Outbox.EnqueueAsync(tx, d, evt, time);

        private async Task<Organisation?> SingleOrg(string sql, object key)
        {
            await using var cmd = tx.Command(sql).Param("k", key);
            await using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            return await r.ReadAsync().ConfigureAwait(false) ? Rows.ReadOrganisation(r, d) : null;
        }
    }
}
