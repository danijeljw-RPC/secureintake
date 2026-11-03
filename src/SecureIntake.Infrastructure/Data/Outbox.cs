using System.Data.Common;
using SecureIntake.Application.Abstractions;

namespace SecureIntake.Infrastructure.Data;

/// <summary>Transactional outbox (F12). Events are written with the business change, then dispatched at least once.</summary>
internal static class Outbox
{
    public static async Task EnqueueAsync(DbTransaction tx, SqlDialect d, OutboxEvent evt, TimeProvider time)
    {
        await using var cmd = tx.Command("""
            INSERT INTO outbox_events (id, organisation_id, type, version, payload, created_at)
            VALUES (@id, @org, @type, @v, @p, @t)
            """)
            .Param("id", d.Id(Guid.CreateVersion7(time.GetUtcNow()))).Param("org", d.Id(evt.OrganisationId))
            .Param("type", evt.Type).Param("v", evt.Version).Param("p", evt.PayloadJson)
            .Param("t", time.GetUtcNow().ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
