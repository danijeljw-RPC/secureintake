using Npgsql;
using SecureIntake.Application.Access;
using SecureIntake.Application.Intake;
using SecureIntake.Application.Organisations;
using SecureIntake.Contracts.Catalogue;
using SecureIntake.Domain.Organisations;
using SecureIntake.Infrastructure.Data;

namespace SecureIntake.IntegrationTests;

/// <summary>
/// A fresh, migrated database per test. SQLite always runs. PostgreSQL runs when
/// <c>SECUREINTAKE_TEST_POSTGRES</c> holds a superuser connection string for a server where the
/// <c>secureintake_app</c> role exists (NOSUPERUSER NOBYPASSRLS) with password <c>SECUREINTAKE_TEST_APP_PASSWORD</c>.
/// Migrations run as the owner; the application runs as <c>secureintake_app</c>, so RLS is really enforced.
/// </summary>
public sealed class TestDb : IAsyncDisposable
{
    private readonly Func<Task> _cleanup;

    private TestDb(Database runtime, Database owner, Func<Task> cleanup)
    {
        Runtime = runtime;
        Owner = owner;
        _cleanup = cleanup;
    }

    public Database Runtime { get; }
    /// <summary>Schema owner. Used only to simulate an attacker with direct database access.</summary>
    public Database Owner { get; }

    public static readonly string? PostgresAdmin = Environment.GetEnvironmentVariable("SECUREINTAKE_TEST_POSTGRES");

    public static TheoryData<string> Providers()
    {
        var data = new TheoryData<string> { "sqlite" };
        if (!string.IsNullOrEmpty(PostgresAdmin))
        {
            data.Add("postgres");
        }
        return data;
    }

    public static async Task<TestDb> CreateAsync(string provider)
    {
        if (provider == "sqlite")
        {
            var dir = Directory.CreateTempSubdirectory("si-test-");
            var path = Path.Combine(dir.FullName, "test.db");
            var db = Database.Sqlite(path);
            await new MigrationRunner(db).ApplyAsync("test-operator");
            return new TestDb(db, db, async () =>
            {
                await db.DisposeAsync();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { dir.Delete(true); } catch (IOException) { }
            });
        }

        var admin = new NpgsqlConnectionStringBuilder(PostgresAdmin);
        var name = "si_test_" + Guid.NewGuid().ToString("N")[..12];
        await using (var conn = new NpgsqlConnection(admin.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        var ownerCs = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = name };
        var appCs = new NpgsqlConnectionStringBuilder(ownerCs.ConnectionString)
        {
            Username = "secureintake_app",
            Password = Environment.GetEnvironmentVariable("SECUREINTAKE_TEST_APP_PASSWORD") ?? "app",
        };
        var owner = Database.Postgres(ownerCs.ConnectionString);
        await new MigrationRunner(owner).ApplyAsync("test-operator");
        var runtime = Database.Postgres(appCs.ConnectionString);
        return new TestDb(runtime, owner, async () =>
        {
            await runtime.DisposeAsync();
            await owner.DisposeAsync();
            NpgsqlConnection.ClearAllPools();
            await using var conn = new NpgsqlConnection(admin.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", conn);
            await cmd.ExecuteNonQueryAsync();
        });
    }

    public ValueTask DisposeAsync() => new(_cleanup());
}

/// <summary>Services wired over a <see cref="TestDb"/>, plus helpers to provision active organisations.</summary>
public sealed class TestPlatform(Database db, TimeProvider? time = null)
{
    public TimeProvider Time { get; } = time ?? TimeProvider.System;
    public Authorizer Authorizer { get; } = new(PermissionCatalogue.Current);
    public ControlPlane ControlPlane => new(db, Time);
    public TenantDataPlane DataPlane => new(db, Time);
    public OrganisationService Organisations => new(ControlPlane, Authorizer, new ProvisioningOptions(false, false, "shared-primary", "shared-primary"), Time);
    public DropboxService Dropboxes => new(ControlPlane, DataPlane, Authorizer, Time);

    public async Task<Organisation> ActiveOrgAsync(string slug)
    {
        var org = await Organisations.CreateAsync(Principal.System, $"Org {slug}", slug, ProvisioningMethod.Manual);
        return await Organisations.ActivateAsync(Principal.System, org.Id);
    }

    public static Principal OwnerOf(Guid org) => new(Guid.CreateVersion7(), PrincipalKind.User, "usr_owner", new HashSet<string>(),
        new Dictionary<Guid, IReadOnlySet<string>> { [org] = new HashSet<string> { "owner" } }, new HashSet<string>(), new HashSet<string>());
}
