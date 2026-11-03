using System.Text;
using Microsoft.Extensions.Time.Testing;
using SecureIntake.Application.Access;
using SecureIntake.Contracts.Catalogue;
using SecureIntake.Infrastructure.Identity;
using SecureIntake.Infrastructure.Jobs;

namespace SecureIntake.IntegrationTests;

public class JobStoreTests
{
    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Enqueue_is_idempotent_and_claim_prefers_essential(string provider)
    {
        await using var db = await TestDb.CreateAsync(provider);
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-03T00:00:00Z"));
        var jobs = new JobStore(db.Runtime, time);
        var bg = await jobs.EnqueueAsync(new JobRequest("t.bg", "{}", IdempotencyKey: "k1"));
        Assert.Equal(bg, await jobs.EnqueueAsync(new JobRequest("t.bg", "{}", IdempotencyKey: "k1")));
        var essential = await jobs.EnqueueAsync(new JobRequest("t.ess", "{}", PriorityClass: PriorityClass.Essential));

        var first = await jobs.ClaimAsync("w1", TimeSpan.FromMinutes(1));
        Assert.Equal(essential, first!.Id);
        var second = await jobs.ClaimAsync("w2", TimeSpan.FromMinutes(1));
        Assert.Equal(bg, second!.Id);
        Assert.Null(await jobs.ClaimAsync("w3", TimeSpan.FromMinutes(1)));

        Assert.False(await jobs.CompleteAsync(first.Id, "someone-else"));
        Assert.True(await jobs.CompleteAsync(first.Id, "w1"));
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Expired_lease_is_reclaimable_and_failures_end_dead(string provider)
    {
        await using var db = await TestDb.CreateAsync(provider);
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-03T00:00:00Z"));
        var jobs = new JobStore(db.Runtime, time);
        var id = await jobs.EnqueueAsync(new JobRequest("t.flaky", "{}", MaxAttempts: 2));

        var claim = await jobs.ClaimAsync("w1", TimeSpan.FromSeconds(30));
        Assert.Null(await jobs.ClaimAsync("w2", TimeSpan.FromSeconds(30)));
        time.Advance(TimeSpan.FromSeconds(31));
        var reclaimed = await jobs.ClaimAsync("w2", TimeSpan.FromSeconds(30));
        Assert.Equal(id, reclaimed!.Id);
        Assert.Equal(2, reclaimed.Attempts);
        Assert.False(await jobs.CompleteAsync(id, "w1")); // the old owner lost its lease

        Assert.True(await jobs.FailAsync(reclaimed, "w2", "boom"));
        var counts = await jobs.CountsByStateAsync();
        Assert.Equal(1, counts["dead"]);
        Assert.True(await jobs.ReplayDeadAsync(id));
        Assert.NotNull(await jobs.ClaimAsync("w3", TimeSpan.FromSeconds(30)));
        _ = claim;
    }
}

public class ServiceAccountTests
{
    private static readonly byte[] Pepper = Encoding.UTF8.GetBytes("test-pepper-test-pepper-test-pepper!");

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Token_authenticates_and_is_stored_only_as_a_digest(string provider)
    {
        await using var db = await TestDb.CreateAsync(provider);
        var p = new TestPlatform(db.Runtime);
        var org = await p.ActiveOrgAsync("tenant-a");
        var store = new ServiceAccountStore(db.Runtime, Pepper, TimeProvider.System);
        var created = await store.CreateOrganisationApiClientAsync(org.Id, "swoovr-test",
            ["org.dropboxes.view", "org.grants.issue_api"], p.Authorizer, "system:test");

        var principal = await store.AuthenticateAsync(created.Token);
        Assert.NotNull(principal);
        Assert.Equal(PrincipalKind.ServiceAccount, principal!.Kind);
        Assert.True(p.Authorizer.Evaluate(principal, "org.grants.issue_api", org.Id).IsAllowed);
        Assert.False(p.Authorizer.Evaluate(principal, "org.webhooks.manage", org.Id).IsAllowed);

        Assert.Null(await store.AuthenticateAsync(created.Token + "x"));
        Assert.Null(await store.AuthenticateAsync(null));
        Assert.Null(await new ServiceAccountStore(db.Runtime, Encoding.UTF8.GetBytes("another-pepper-another-pepper-12345"), TimeProvider.System)
            .AuthenticateAsync(created.Token));

        var raw = await db.Owner.InTransactionAsync(async tx =>
        {
            await using var c = tx.Connection!.CreateCommand();
            c.Transaction = tx;
            c.CommandText = "SELECT token_digest FROM service_accounts";
            return (byte[])(await c.ExecuteScalarAsync())!;
        });
        Assert.DoesNotContain(created.Token, Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_client_cannot_be_created_with_sensitive_or_platform_permissions()
    {
        await using var db = await TestDb.CreateAsync("sqlite");
        var p = new TestPlatform(db.Runtime);
        var org = await p.ActiveOrgAsync("tenant-a");
        var store = new ServiceAccountStore(db.Runtime, Pepper, TimeProvider.System);
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateOrganisationApiClientAsync(org.Id, "x", ["org.submissions.delete"], p.Authorizer, "t"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateOrganisationApiClientAsync(org.Id, "x", ["platform.organisations.provision"], p.Authorizer, "t"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreatePlatformAsync("x", ["owner"], "t"));
    }
}
