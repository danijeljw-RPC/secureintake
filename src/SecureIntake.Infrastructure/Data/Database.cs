using System.Data.Common;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace SecureIntake.Infrastructure.Data;

/// <summary>Connection factory for one database. Runtime connections must use a role that can't bypass RLS.</summary>
public sealed class Database : IAsyncDisposable
{
    private readonly NpgsqlDataSource? _pg;
    private readonly string _connectionString;

    private Database(SqlDialect dialect, string connectionString, NpgsqlDataSource? pg, string? sqlitePath = null)
    {
        SqlitePath = sqlitePath;
        Dialect = dialect;
        _connectionString = connectionString;
        _pg = pg;
    }

    public SqlDialect Dialect { get; }

    /// <summary>Full path of the SQLite file, or null for PostgreSQL.</summary>
    public string? SqlitePath { get; }

    public static Database Sqlite(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true }.ToString();
        return new Database(SqlDialect.Sqlite, cs, null, Path.GetFullPath(path));
    }

    public static Database Postgres(string connectionString) =>
        new(SqlDialect.Postgres, connectionString, new NpgsqlSlimDataSourceBuilder(connectionString).Build());

    public async Task<DbConnection> OpenAsync(CancellationToken ct = default)
    {
        if (_pg is not null)
        {
            return await _pg.OpenConnectionAsync(ct).ConfigureAwait(false);
        }
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000; PRAGMA synchronous = FULL;";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return conn;
    }

    /// <summary>
    /// Runs <paramref name="work"/> in a transaction and commits it. SQLite transactions start IMMEDIATE so
    /// writers serialise, which is what makes the audit chain head safe without row locks.
    /// </summary>
    public async Task<T> InTransactionAsync<T>(Func<DbTransaction, Task<T>> work, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = conn is SqliteConnection sqlite
            ? sqlite.BeginTransaction(deferred: false)
            : await conn.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        var result = await work(tx).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (_pg is not null)
        {
            await _pg.DisposeAsync().ConfigureAwait(false);
        }
    }
}
