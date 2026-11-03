using System.Data.Common;
using SecureIntake.Application.Abstractions;
using SecureIntake.Cryptography.Signing;
using SecureIntake.Infrastructure.Audit;

namespace SecureIntake.IntegrationTests;

public class FencingTests
{
    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Fenced_tenant_rejects_writes_but_allows_reads(string provider)
    {
        await using var db = await TestDb.CreateAsync(provider);
        var p = new TestPlatform(db.Runtime);
        var a = await p.ActiveOrgAsync("tenant-a");
        await p.Dropboxes.CreateAsync(TestPlatform.OwnerOf(a.Id), a.Id, "before");

        await p.ControlPlane.ExecuteAsync(async tx =>
        {
            var current = (await tx.FindAssignmentAsync(a.Id))!;
            await tx.UpsertAssignmentAsync(current with { WritesBlocked = true, Generation = current.Generation + 1 });
            return 0;
        });

        await Assert.ThrowsAsync<TenantFencedException>(() => p.Dropboxes.CreateAsync(TestPlatform.OwnerOf(a.Id), a.Id, "after"));
        Assert.Single(await p.Dropboxes.ListAsync(TestPlatform.OwnerOf(a.Id), a.Id, 10));
        Assert.Equal(2, await p.DataPlane.ExecuteAsync(a.Id, tx => Task.FromResult(tx.Generation)));
    }
}

public class AuditIntegrationTests
{
    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Chain_and_checkpoints_verify_and_go_to_the_sink(string provider)
    {
        await using var db = await TestDb.CreateAsync(provider);
        var p = new TestPlatform(db.Runtime);
        var a = await p.ActiveOrgAsync("tenant-a");
        await p.Dropboxes.CreateAsync(TestPlatform.OwnerOf(a.Id), a.Id, "one");

        using var key = Es256Signer.Generate("audck_test");
        var sinkPath = Path.Combine(Path.GetTempPath(), $"si-sink-{Guid.NewGuid():N}.jsonl");
        using var sink = new FileCheckpointSink(sinkPath);
        var writer = new CheckpointWriter(db.Runtime, key, sink, TimeProvider.System);
        Assert.Equal(2, await writer.CheckpointAllAsync()); // platform + tenant-a
        Assert.Equal(0, await writer.CheckpointAllAsync()); // nothing advanced
        Assert.Equal(2, File.ReadAllLines(sinkPath).Length);

        var verifier = new AuditVerifier(db.Runtime);
        var platform = await verifier.VerifyPlatformAsync(id => id == key.KeyId ? key.Verifier() : null);
        Assert.True(platform.IsValid);
        Assert.Equal(1, platform.CheckpointsVerified);
        Assert.True(platform.Records >= 4);

        // A checkpoint signed by an untrusted key fails verification.
        var untrusted = await verifier.VerifyPlatformAsync(_ => null);
        Assert.False(untrusted.IsValid);
        File.Delete(sinkPath);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Tampering_with_a_stored_record_is_detected(string provider)
    {
        await using var db = await TestDb.CreateAsync(provider);
        var p = new TestPlatform(db.Runtime);
        await p.ActiveOrgAsync("tenant-a");

        // Simulate an attacker with owner-level database access editing history.
        await db.Owner.InTransactionAsync(async tx =>
        {
            if (provider == "sqlite")
            {
                await Exec(tx, "DROP TRIGGER platform_audit_no_update");
            }
            return await Exec(tx, "UPDATE platform_audit SET record_json = replace(record_json, 'test-operator', 'evil-operator') WHERE seq = 1");
        });

        var result = await new AuditVerifier(db.Runtime).VerifyPlatformAsync(_ => null);
        Assert.False(result.IsValid);
        Assert.Equal(1, result.FirstBadSeq);
    }

    [Fact]
    public async Task Sqlite_triggers_block_audit_updates_and_deletes()
    {
        await using var db = await TestDb.CreateAsync("sqlite");
        await new TestPlatform(db.Runtime).ActiveOrgAsync("tenant-a");
        await Assert.ThrowsAnyAsync<DbException>(() => db.Runtime.InTransactionAsync(tx => Exec(tx, "UPDATE platform_audit SET event = 'x'")));
        await Assert.ThrowsAnyAsync<DbException>(() => db.Runtime.InTransactionAsync(tx => Exec(tx, "DELETE FROM platform_audit")));
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Audit_failure_rolls_back_the_business_change(string provider)
    {
        await using var db = await TestDb.CreateAsync(provider);
        var p = new TestPlatform(db.Runtime);
        var a = await p.ActiveOrgAsync("tenant-a");
        await Assert.ThrowsAsync<ArgumentException>(() => p.DataPlane.ExecuteAsync(a.Id, async tx =>
        {
            await tx.InsertDropboxAsync(new SecureIntake.Domain.Intake.Dropbox(Guid.CreateVersion7(), a.Id, "x", SecureIntake.Domain.Intake.DropboxStatus.Draft, 1));
            // Unsupported data type makes the audit write fail, which must abort the whole transaction.
            await tx.AppendAuditAsync(new AuditEntry("dropbox.created", "test", null, new Dictionary<string, object?> { ["bad"] = 1.5 }));
            return 0;
        }));
        Assert.Empty(await p.Dropboxes.ListAsync(TestPlatform.OwnerOf(a.Id), a.Id, 10));
    }

    private static async Task<int> Exec(DbTransaction tx, string sql)
    {
        await using var c = tx.Connection!.CreateCommand();
        c.Transaction = tx;
        c.CommandText = sql;
        return await c.ExecuteNonQueryAsync();
    }
}
