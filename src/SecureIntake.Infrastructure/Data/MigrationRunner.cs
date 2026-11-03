using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using SecureIntake.Application.Abstractions;
using SecureIntake.Infrastructure.Audit;

namespace SecureIntake.Infrastructure.Data;

public sealed record AppliedMigration(int Version, string Name, string Checksum);

public enum SchemaState
{
    /// <summary>Every migration this build knows is applied, with matching checksums.</summary>
    Current,
    /// <summary>The database exists but migrations are outstanding.</summary>
    MigrationsPending,
    /// <summary>No schema_migrations table: the database was never migrated.</summary>
    Uninitialised,
    /// <summary>The database has migrations this build doesn't know (a newer build migrated it).</summary>
    NewerThanBuild,
    /// <summary>An applied migration's checksum differs from this build's script.</summary>
    ChecksumMismatch,
    /// <summary>The database couldn't be queried.</summary>
    Unreachable,
}

/// <summary>Schema compatibility. Contains no connection details, so it's safe to log and to show to operators.</summary>
public sealed record SchemaStatus(SchemaState State, IReadOnlyList<int> Pending, IReadOnlyList<int> Unknown, IReadOnlyList<int> Mismatched,
    IReadOnlyList<int> InterruptedAttempts, string? ErrorClass)
{
    public bool IsCompatible => State == SchemaState.Current;

    /// <summary>Stable reason code for readiness diagnostics, for example <c>schema.migrations_pending</c>.</summary>
    public string ReasonCode => State switch
    {
        SchemaState.Current => "schema.current",
        SchemaState.MigrationsPending => "schema.migrations_pending",
        SchemaState.Uninitialised => "schema.uninitialised",
        SchemaState.NewerThanBuild => "schema.newer_than_build",
        SchemaState.ChecksumMismatch => "schema.checksum_mismatch",
        _ => "database.unreachable",
    };
}

public sealed class MigrationLockedException : Exception
{
    public MigrationLockedException() : base("Another migration process holds the migration lock.") { }
    public MigrationLockedException(string message) : base(message) { }
    public MigrationLockedException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Forward-only migrations from embedded SQL (ADR-0007). Each script runs in its own transaction together with
/// its <c>schema_migrations</c> row and a platform audit record, so a failure leaves nothing partially applied.
/// Every attempt is logged in <c>schema_migration_log</c> (started → succeeded | failed); a <c>started</c> row with no
/// outcome marks an interrupted run. A cross-process lock prevents concurrent runs: a PostgreSQL advisory lock,
/// or an exclusive lock file beside the SQLite database.
/// <para>Production never calls <see cref="ApplyAsync"/> implicitly; only <c>secureintake migrate</c> does.</para>
/// </summary>
public sealed class MigrationRunner(Database database, TimeProvider? time = null)
{
    /// <summary>Advisory lock key: the first 8 bytes of SHA-256("secureintake.migrate").</summary>
    private static readonly long AdvisoryLockKey = BitConverter.ToInt64(SHA256.HashData("secureintake.migrate"u8), 0);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public IReadOnlyList<(int Version, string Name, string Sql)> Available()
    {
        var asm = typeof(MigrationRunner).Assembly;
        var prefix = $"SecureIntake.Infrastructure.Data.Migrations.{database.Dialect.MigrationFolder}.";
        return [.. asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n =>
            {
                using var s = asm.GetManifestResourceStream(n)!;
                using var reader = new StreamReader(s, Encoding.UTF8);
                var name = Path.GetFileNameWithoutExtension(n[prefix.Length..]).TrimStart('_');
                return (int.Parse(name[..4], System.Globalization.CultureInfo.InvariantCulture), name, reader.ReadToEnd());
            })
            .OrderBy(m => m.Item1)];
    }

    /// <summary>Read-only compatibility check. Safe for the runtime role, which can't create tables.</summary>
    public async Task<SchemaStatus> StatusAsync(CancellationToken ct = default)
    {
        List<AppliedMigration> applied;
        var interrupted = new List<int>();
        try
        {
            await using var conn = await database.OpenAsync(ct).ConfigureAwait(false);
            if (!await TableExistsAsync(conn, "schema_migrations", ct).ConfigureAwait(false))
            {
                return new(SchemaState.Uninitialised, [.. Available().Select(a => a.Version)], [], [], [], null);
            }
            applied = await ReadAppliedAsync(conn, null, ct).ConfigureAwait(false);
            if (await TableExistsAsync(conn, "schema_migration_log", ct).ConfigureAwait(false))
            {
                interrupted = await ReadInterruptedAsync(conn, ct).ConfigureAwait(false);
            }
        }
        catch (DbException ex)
        {
            return new(SchemaState.Unreachable, [], [], [], [], ex.GetType().Name);
        }
        catch (TimeoutException ex)
        {
            return new(SchemaState.Unreachable, [], [], [], [], ex.GetType().Name);
        }

        var available = Available();
        var byVersion = applied.ToDictionary(a => a.Version);
        var pending = available.Where(a => !byVersion.ContainsKey(a.Version)).Select(a => a.Version).ToList();
        var unknown = applied.Where(a => available.All(x => x.Version != a.Version)).Select(a => a.Version).ToList();
        var mismatched = available.Where(a => byVersion.TryGetValue(a.Version, out var x) && x.Checksum != Checksum(a.Sql))
            .Select(a => a.Version).ToList();
        interrupted.RemoveAll(v => byVersion.ContainsKey(v));

        var state = mismatched.Count > 0 ? SchemaState.ChecksumMismatch
            : unknown.Count > 0 ? SchemaState.NewerThanBuild
            : pending.Count > 0 ? SchemaState.MigrationsPending
            : SchemaState.Current;
        return new(state, pending, unknown, mismatched, interrupted, null);
    }

    /// <summary>Kept for readiness callers: true only when the schema exactly matches this build.</summary>
    public async Task<bool> IsCurrentAsync(CancellationToken ct = default) => (await StatusAsync(ct).ConfigureAwait(false)).IsCompatible;

    public Task<IReadOnlyList<AppliedMigration>> AppliedAsync(CancellationToken ct = default) =>
        WithConnection<IReadOnlyList<AppliedMigration>>(async conn => await ReadAppliedAsync(conn, null, ct).ConfigureAwait(false), ct);

    /// <summary>
    /// Applies pending migrations under the migration lock and returns their names. <paramref name="operatorIdentity"/>
    /// is recorded in the run log and the audit trail. Throws <see cref="MigrationLockedException"/> if another
    /// process is migrating, and stops at the first failure after recording it.
    /// </summary>
    public async Task<IReadOnlyList<string>> ApplyAsync(string operatorIdentity, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorIdentity);
        await using var conn = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var fileLock = AcquireSqliteLock();
        await AcquireAdvisoryLockAsync(conn, ct).ConfigureAwait(false);
        try
        {
            await EnsureBootstrapTablesAsync(conn, ct).ConfigureAwait(false);
            var done = new List<string>();
            foreach (var (version, name, sql) in Available())
            {
                var checksum = Checksum(sql);
                var applied = (await ReadAppliedAsync(conn, null, ct).ConfigureAwait(false)).ToDictionary(a => a.Version);
                if (applied.TryGetValue(version, out var existing))
                {
                    if (existing.Checksum != checksum)
                    {
                        throw new InvalidOperationException($"Migration {name} was changed after it was applied.");
                    }
                    continue;
                }
                await ApplyOneAsync(conn, version, name, sql, checksum, operatorIdentity, ct).ConfigureAwait(false);
                done.Add(name);
            }
            var unknown = (await ReadAppliedAsync(conn, null, ct).ConfigureAwait(false)).Where(a => Available().All(x => x.Version != a.Version)).ToList();
            if (unknown.Count > 0)
            {
                throw new InvalidOperationException($"The database has migrations this build doesn't know ({string.Join(", ", unknown.Select(u => u.Name))}). Use a newer build.");
            }
            return done;
        }
        finally
        {
            await ReleaseAdvisoryLockAsync(conn).ConfigureAwait(false);
        }
    }

    private async Task ApplyOneAsync(DbConnection conn, int version, string name, string sql, string checksum, string operatorIdentity, CancellationToken ct)
    {
        var attemptId = Guid.CreateVersion7(_time.GetUtcNow()).ToString("D");
        await ExecAsync(conn, null, """
            INSERT INTO schema_migration_log (id, version, name, checksum, operator, started_at, outcome)
            VALUES (@id, @v, @n, @c, @o, @t, 'started')
            """, ct, ("id", attemptId), ("v", version), ("n", name), ("c", checksum), ("o", operatorIdentity), ("t", Now())).ConfigureAwait(false);
        try
        {
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            await ExecAsync(conn, tx, sql, ct).ConfigureAwait(false);
            await ExecAsync(conn, tx, "INSERT INTO schema_migrations (version, name, checksum, applied_at) VALUES (@v, @n, @c, @t)", ct,
                ("v", version), ("n", name), ("c", checksum), ("t", Now())).ConfigureAwait(false);
            // Every schema contains the audit tables from 0001 on, so each migration is audited in its own transaction.
            await AuditRecordWriter.AppendPlatformAsync(tx, database.Dialect, AuditEntry.Of("schema.migration_applied", operatorIdentity, null,
                ("version", version), ("name", name), ("checksum", checksum)), _time).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            await ExecAsync(conn, null, "UPDATE schema_migration_log SET outcome = 'failed', finished_at = @t, error = @e WHERE id = @id", CancellationToken.None,
                ("t", Now()), ("e", SafeError(ex)), ("id", attemptId)).ConfigureAwait(false);
            throw;
        }
        await ExecAsync(conn, null, "UPDATE schema_migration_log SET outcome = 'succeeded', finished_at = @t WHERE id = @id", ct,
            ("t", Now()), ("id", attemptId)).ConfigureAwait(false);
    }

    /// <summary>Error class and SQLSTATE only, so connection details can't leak into the log.</summary>
    private static string SafeError(Exception ex) =>
        ex is Npgsql.PostgresException pg ? $"{ex.GetType().Name} {pg.SqlState}" : ex.GetType().Name;

    private async Task<T> WithConnection<T>(Func<DbConnection, Task<T>> work, CancellationToken ct)
    {
        await using var conn = await database.OpenAsync(ct).ConfigureAwait(false);
        return await work(conn).ConfigureAwait(false);
    }

    private static async Task EnsureBootstrapTablesAsync(DbConnection conn, CancellationToken ct)
    {
        await ExecAsync(conn, null, "CREATE TABLE IF NOT EXISTS schema_migrations (version integer PRIMARY KEY, name text NOT NULL, checksum text NOT NULL, applied_at bigint NOT NULL)", ct).ConfigureAwait(false);
        await ExecAsync(conn, null, """
            CREATE TABLE IF NOT EXISTS schema_migration_log (
                id text PRIMARY KEY, version integer NOT NULL, name text NOT NULL, checksum text NOT NULL, operator text NOT NULL,
                started_at bigint NOT NULL, finished_at bigint, outcome text NOT NULL, error text)
            """, ct).ConfigureAwait(false);
    }

    private async Task<bool> TableExistsAsync(DbConnection conn, string table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = database.Dialect.Provider == DatabaseProvider.Postgres
            ? "SELECT count(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = @t"
            : "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = @t";
        cmd.Param("t", table);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<List<AppliedMigration>> ReadAppliedAsync(DbConnection conn, DbTransaction? tx, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT version, name, checksum FROM schema_migrations ORDER BY version";
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<AppliedMigration>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new AppliedMigration(r.GetInt32(0), r.GetString(1), r.GetString(2)));
        }
        return list;
    }

    private static async Task<List<int>> ReadInterruptedAsync(DbConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT version FROM schema_migration_log WHERE outcome = 'started'";
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<int>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(r.GetInt32(0));
        }
        return list;
    }

    private async Task AcquireAdvisoryLockAsync(DbConnection conn, CancellationToken ct)
    {
        if (database.Dialect.Provider != DatabaseProvider.Postgres)
        {
            return;
        }
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pg_try_advisory_lock(@k)";
        cmd.Param("k", AdvisoryLockKey);
        if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not true)
        {
            throw new MigrationLockedException();
        }
    }

    private async Task ReleaseAdvisoryLockAsync(DbConnection conn)
    {
        if (database.Dialect.Provider != DatabaseProvider.Postgres)
        {
            return;
        }
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pg_advisory_unlock(@k)";
        cmd.Param("k", AdvisoryLockKey);
        await cmd.ExecuteScalarAsync().ConfigureAwait(false);
    }

    /// <summary>Exclusive lock file beside the SQLite database. The OS releases it if the process dies.</summary>
    private FileStream? AcquireSqliteLock()
    {
        if (database.SqlitePath is not { } path)
        {
            return null;
        }
        try
        {
            return new FileStream(path + ".migrate.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException ex)
        {
            throw new MigrationLockedException("Another migration process holds the migration lock file.", ex);
        }
    }

    private static async Task ExecAsync(DbConnection conn, DbTransaction? tx, string sql, CancellationToken ct, params (string Name, object? Value)[] ps)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in ps)
        {
            cmd.Param(n, v);
        }
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private long Now() => _time.GetUtcNow().ToUnixTimeMilliseconds();

    internal static string Checksum(string sql) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql.ReplaceLineEndings("\n"))));
}
