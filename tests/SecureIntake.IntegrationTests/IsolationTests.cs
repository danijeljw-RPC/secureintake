using System.Data.Common;
using SecureIntake.Application.Abstractions;
using SecureIntake.Application.Access;
using SecureIntake.Domain.Intake;
using SecureIntake.Infrastructure.Data;

namespace SecureIntake.IntegrationTests;

/// <summary>Cross-tenant isolation suite (M1 exit criterion). Runs on every configured database.</summary>
public class IsolationTests
{
    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Tenant_cannot_read_another_tenants_dropboxes(string provider)
    {
        await using var db = await TestDb.CreateAsync(provider);
        var p = new TestPlatform(db.Runtime);
        var a = await p.ActiveOrgAsync("tenant-a");
        var b = await p.ActiveOrgAsync("tenant-b");
        var dbxA = await p.Dropboxes.CreateAsync(TestPlatform.OwnerOf(a.Id), a.Id, "A intake");
        var dbxB = await p.Dropboxes.CreateAsync(TestPlatform.OwnerOf(b.Id), b.Id, "B intake");

        var listA = await p.Dropboxes.ListAsync(TestPlatform.OwnerOf(a.Id), a.Id, 100);
        Assert.Equal([dbxA.Id], listA.Select(d => d.Id));

        // Even with a known ID, A's transaction can't see B's dropbox.
        Assert.Null(await p.DataPlane.ExecuteAsync(a.Id, tx => tx.FindDropboxAsync(dbxB.Id)));
        Assert.NotNull(await p.DataPlane.ExecuteAsync(b.Id, tx => tx.FindDropboxAsync(dbxB.Id)));
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Member_of_one_tenant_is_denied_in_another(string provider)
    {
        await using var db = await TestDb.CreateAsync(provider);
        var p = new TestPlatform(db.Runtime);
        var a = await p.ActiveOrgAsync("tenant-a");
        var b = await p.ActiveOrgAsync("tenant-b");
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => p.Dropboxes.ListAsync(TestPlatform.OwnerOf(a.Id), b.Id, 10));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => p.Dropboxes.CreateAsync(TestPlatform.OwnerOf(a.Id), b.Id, "x"));
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Writing_another_tenants_entity_is_refused(string provider)
    {
        await using var db = await TestDb.CreateAsync(provider);
        var p = new TestPlatform(db.Runtime);
        var a = await p.ActiveOrgAsync("tenant-a");
        var b = await p.ActiveOrgAsync("tenant-b");
        var foreign = new Dropbox(Guid.CreateVersion7(), b.Id, "smuggled", DropboxStatus.Draft, 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => p.DataPlane.ExecuteAsync(a.Id, async tx =>
        {
            await tx.InsertDropboxAsync(foreign);
            return 0;
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => p.DataPlane.ExecuteAsync(a.Id, async tx =>
        {
            await tx.EnqueueOutboxAsync(new OutboxEvent("x", 1, b.Id, "{}"));
            return 0;
        }));
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Tenant_audit_chains_are_separate(string provider)
    {
        await using var db = await TestDb.CreateAsync(provider);
        var p = new TestPlatform(db.Runtime);
        var a = await p.ActiveOrgAsync("tenant-a");
        var b = await p.ActiveOrgAsync("tenant-b");
        await p.Dropboxes.CreateAsync(TestPlatform.OwnerOf(a.Id), a.Id, "A1");
        await p.Dropboxes.CreateAsync(TestPlatform.OwnerOf(a.Id), a.Id, "A2");
        await p.Dropboxes.CreateAsync(TestPlatform.OwnerOf(b.Id), b.Id, "B1");

        var verifier = new SecureIntake.Infrastructure.Audit.AuditVerifier(db.Runtime);
        var ra = await verifier.VerifyOrganisationAsync(a.Id, _ => null);
        var rb = await verifier.VerifyOrganisationAsync(b.Id, _ => null);
        Assert.True(ra.IsValid);
        Assert.True(rb.IsValid);
        Assert.Equal(2, ra.Records);
        Assert.Equal(1, rb.Records);
    }

    /// <summary>Defence in depth: with application filters bypassed, PostgreSQL RLS still confines the runtime role.</summary>
    [SkippableFactPostgres]
    public async Task Rls_confines_raw_sql_to_the_bound_tenant()
    {
        await using var db = await TestDb.CreateAsync("postgres");
        var p = new TestPlatform(db.Runtime);
        var a = await p.ActiveOrgAsync("tenant-a");
        var b = await p.ActiveOrgAsync("tenant-b");
        await p.Dropboxes.CreateAsync(TestPlatform.OwnerOf(a.Id), a.Id, "A1");
        await p.Dropboxes.CreateAsync(TestPlatform.OwnerOf(b.Id), b.Id, "B1");
        await p.Dropboxes.CreateAsync(TestPlatform.OwnerOf(b.Id), b.Id, "B2");

        async Task<long> CountAs(Guid? tenant) => await db.Runtime.InTransactionAsync(async tx =>
        {
            if (tenant is { } t)
            {
                await db.Runtime.Dialect.SetTenantContextAsync(tx.Connection!, tx, t);
            }
            await using var cmd = Cmd(tx, "SELECT count(*) FROM dropboxes");
            return (long)(await cmd.ExecuteScalarAsync())!;
        });

        Assert.Equal(0, await CountAs(null));
        Assert.Equal(1, await CountAs(a.Id));
        Assert.Equal(2, await CountAs(b.Id));

        // Inserting a row for B while bound to A violates the policy's WITH CHECK.
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Runtime.InTransactionAsync(async tx =>
        {
            await db.Runtime.Dialect.SetTenantContextAsync(tx.Connection!, tx, a.Id);
            await using var cmd = Cmd(tx, $"INSERT INTO dropboxes (id, organisation_id, name, status, version, created_at, updated_at) VALUES ('{Guid.CreateVersion7()}', '{b.Id}', 'x', 'Draft', 1, 0, 0)");
            return await cmd.ExecuteNonQueryAsync();
        }));
    }

    [SkippableFactPostgres]
    public async Task Runtime_role_cannot_rewrite_audit_history()
    {
        await using var db = await TestDb.CreateAsync("postgres");
        var p = new TestPlatform(db.Runtime);
        await p.ActiveOrgAsync("tenant-a");
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Runtime.InTransactionAsync(async tx =>
        {
            await using var cmd = Cmd(tx, "UPDATE platform_audit SET event = 'forged' WHERE seq = 1");
            return await cmd.ExecuteNonQueryAsync();
        }));
        Assert.Equal("42501", ex.SqlState); // insufficient_privilege
    }

    private static DbCommand Cmd(DbTransaction tx, string sql)
    {
        var c = tx.Connection!.CreateCommand();
        c.Transaction = tx;
        c.CommandText = sql;
        return c;
    }
}

public sealed class SkippableFactPostgresAttribute : FactAttribute
{
    public SkippableFactPostgresAttribute()
    {
        if (string.IsNullOrEmpty(TestDb.PostgresAdmin))
        {
            Skip = "SECUREINTAKE_TEST_POSTGRES not set";
        }
    }
}
