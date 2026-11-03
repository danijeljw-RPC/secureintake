using System.Data.Common;
using SecureIntake.Infrastructure.Data;

namespace SecureIntake.IntegrationTests;

/// <summary>ADR-0007: explicit, locked, logged, audited, all-or-nothing migrations.</summary>
public class MigrationTests
{
    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Migrations_are_idempotent_and_current(string provider)
    {
        await using var db = await TestDb.CreateAsync(provider);
        var runner = new MigrationRunner(db.Owner);
        Assert.Empty(await runner.ApplyAsync("test-operator"));
        Assert.Equal(SchemaState.Current, (await runner.StatusAsync()).State);
        // The runtime role can read status but holds no DDL rights.
        Assert.Equal(SchemaState.Current, (await new MigrationRunner(db.Runtime).StatusAsync()).State);
    }

    [Fact]
    public async Task Fresh_database_is_uninitialised_until_migrated()
    {
        var dir = Directory.CreateTempSubdirectory("si-mig-");
        await using var db = Database.Sqlite(Path.Combine(dir.FullName, "x.db"));
        var runner = new MigrationRunner(db);
        var status = await runner.StatusAsync();
        Assert.Equal(SchemaState.Uninitialised, status.State);
        Assert.Equal("schema.uninitialised", status.ReasonCode);
        Assert.Equal(runner.Available().Select(a => a.Version), status.Pending);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Each_migration_is_logged_and_audited_with_the_operator(string provider)
    {
        await using var db = await TestDb.CreateAsync(provider);
        var log = await Query(db.Owner, "SELECT version, outcome, operator FROM schema_migration_log ORDER BY version",
            r => (r.GetInt32(0), r.GetString(1), r.GetString(2)));
        Assert.Equal([(1, "succeeded", "test-operator"), (2, "succeeded", "test-operator")], log);
        var audit = await Query(db.Owner, "SELECT record_json FROM platform_audit WHERE event = 'schema.migration_applied' ORDER BY seq", r => r.GetString(0));
        Assert.Equal(2, audit.Count);
        Assert.Contains("\"actor\":\"test-operator\"", audit[0], StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Edited_or_unknown_migrations_make_the_schema_incompatible(string provider)
    {
        await using var db = await TestDb.CreateAsync(provider);
        var runner = new MigrationRunner(db.Owner);
        await Exec(db.Owner, "INSERT INTO schema_migrations (version, name, checksum, applied_at) VALUES (9999, '9999_future', 'x', 0)");
        Assert.Equal(SchemaState.NewerThanBuild, (await runner.StatusAsync()).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ApplyAsync("test-operator"));

        await Exec(db.Owner, "UPDATE schema_migrations SET checksum = 'tampered' WHERE version = 1");
        Assert.Equal(SchemaState.ChecksumMismatch, (await runner.StatusAsync()).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ApplyAsync("test-operator"));
    }

    /// <summary>A failing migration leaves no partial schema, records the failure, and can be retried.</summary>
    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Failed_migration_rolls_back_completely_and_is_recorded(string provider)
    {
        await using var fresh = await FreshAsync(provider);
        var db = fresh.Db;
        // Sabotage: a table 0001 wants to create already exists, so 0001 fails part-way through.
        await Exec(db, "CREATE TABLE organisations (id integer)");
        var runner = new MigrationRunner(db);
        await Assert.ThrowsAnyAsync<DbException>(() => runner.ApplyAsync("test-operator"));

        var status = await runner.StatusAsync();
        Assert.Equal(SchemaState.MigrationsPending, status.State);
        Assert.Contains(1, status.Pending);
        Assert.Equal(0L, await Scalar(db, provider == "sqlite"
            ? "SELECT count(*) FROM sqlite_master WHERE name = 'deployment_state'"
            : "SELECT count(*) FROM information_schema.tables WHERE table_name = 'deployment_state'"));
        var log = await Query(db, "SELECT outcome, error FROM schema_migration_log", r => (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)));
        Assert.Equal("failed", Assert.Single(log).Item1);
        Assert.DoesNotContain("Password", log[0].Item2 ?? "", StringComparison.OrdinalIgnoreCase);

        await Exec(db, "DROP TABLE organisations");
        Assert.Equal(["0001_initial", "0002_harden_privileges"], await runner.ApplyAsync("test-operator"));
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Interrupted_attempts_are_reported(string provider)
    {
        await using var db = await TestDb.CreateAsync(provider);
        await Exec(db.Owner, "INSERT INTO schema_migration_log (id, version, name, checksum, operator, started_at, outcome) VALUES ('x', 3, '0003_x', 'c', 'op', 0, 'started')");
        Assert.Equal([3], (await new MigrationRunner(db.Owner).StatusAsync()).InterruptedAttempts);
    }

    [Fact]
    public async Task Concurrent_sqlite_migration_is_refused()
    {
        await using var fresh = await FreshAsync("sqlite");
        await using var held = new FileStream(fresh.Db.SqlitePath + ".migrate.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<MigrationLockedException>(() => new MigrationRunner(fresh.Db).ApplyAsync("test-operator"));
    }

    [SkippableFactPostgres]
    public async Task Concurrent_postgres_migration_is_refused()
    {
        await using var db = await TestDb.CreateAsync("postgres");
        await using var holder = await db.Owner.OpenAsync();
        await using (var cmd = holder.CreateCommand())
        {
            cmd.CommandText = "SELECT pg_advisory_lock(@k)";
            var p = cmd.CreateParameter();
            p.ParameterName = "k";
            p.Value = BitConverter.ToInt64(System.Security.Cryptography.SHA256.HashData("secureintake.migrate"u8), 0);
            cmd.Parameters.Add(p);
            await cmd.ExecuteScalarAsync();
        }
        await Assert.ThrowsAsync<MigrationLockedException>(() => new MigrationRunner(db.Owner).ApplyAsync("test-operator"));
    }

    [SkippableFactPostgres]
    public async Task Runtime_role_has_no_schema_privileges_but_owner_does()
    {
        await using var db = await TestDb.CreateAsync("postgres");
        Assert.Empty(await RuntimeRoleCheck.CheckAsync(db.Runtime));
        Assert.Contains("runtime_role.superuser", await RuntimeRoleCheck.CheckAsync(db.Owner));
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Exec(db.Runtime, "CREATE TABLE sneaky (id int)"));
        Assert.Equal("42501", ex.SqlState);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private sealed class Fresh(Database db, Func<Task> cleanup) : IAsyncDisposable
    {
        public Database Db { get; } = db;
        public ValueTask DisposeAsync() => new(cleanup());
    }

    /// <summary>An empty, never-migrated database.</summary>
    private static async Task<Fresh> FreshAsync(string provider)
    {
        if (provider == "sqlite")
        {
            var dir = Directory.CreateTempSubdirectory("si-mig-");
            var db = Database.Sqlite(Path.Combine(dir.FullName, "x.db"));
            return new Fresh(db, async () => { await db.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); });
        }
        var admin = new Npgsql.NpgsqlConnectionStringBuilder(TestDb.PostgresAdmin);
        var name = "si_fresh_" + Guid.NewGuid().ToString("N")[..12];
        await using (var c = new Npgsql.NpgsqlConnection(admin.ConnectionString))
        {
            await c.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand($"CREATE DATABASE {name}", c);
            await cmd.ExecuteNonQueryAsync();
        }
        var pg = Database.Postgres(new Npgsql.NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = name }.ConnectionString);
        return new Fresh(pg, async () =>
        {
            await pg.DisposeAsync();
            Npgsql.NpgsqlConnection.ClearAllPools();
            await using var c = new Npgsql.NpgsqlConnection(admin.ConnectionString);
            await c.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", c);
            await cmd.ExecuteNonQueryAsync();
        });
    }

    private static async Task Exec(Database db, string sql)
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(Database db, string sql)
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<List<T>> Query<T>(Database db, string sql, Func<DbDataReader, T> map)
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<T>();
        while (await r.ReadAsync())
        {
            list.Add(map(r));
        }
        return list;
    }
}
