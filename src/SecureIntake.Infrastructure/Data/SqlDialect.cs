using System.Data.Common;

namespace SecureIntake.Infrastructure.Data;

public enum DatabaseProvider
{
    Sqlite,
    Postgres,
}

/// <summary>The few places where SQLite and PostgreSQL differ. Queries are otherwise shared.</summary>
public abstract class SqlDialect
{
    public static readonly SqlDialect Sqlite = new SqliteDialect();
    public static readonly SqlDialect Postgres = new PostgresDialect();

    public abstract DatabaseProvider Provider { get; }
    public abstract string MigrationFolder { get; }
    /// <summary>Row lock suffix. SQLite serialises writers with BEGIN IMMEDIATE instead.</summary>
    public abstract string ForUpdate { get; }
    public abstract string ForUpdateSkipLocked { get; }

    public abstract object Id(Guid id);
    public abstract Guid ReadId(DbDataReader reader, int ordinal);

    public object? Id(Guid? id) => id is { } v ? Id(v) : null;

    public Guid? ReadNullableId(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : ReadId(reader, ordinal);

    /// <summary>Binds the transaction to one organisation for RLS. No-op on SQLite.</summary>
    public abstract Task SetTenantContextAsync(DbConnection connection, DbTransaction tx, Guid organisationId);

    private sealed class SqliteDialect : SqlDialect
    {
        public override DatabaseProvider Provider => DatabaseProvider.Sqlite;
        public override string MigrationFolder => "sqlite";
        public override string ForUpdate => "";
        public override string ForUpdateSkipLocked => "";
        public override object Id(Guid id) => id.ToString("D");
        public override Guid ReadId(DbDataReader reader, int ordinal) => Guid.Parse(reader.GetString(ordinal));
        public override Task SetTenantContextAsync(DbConnection connection, DbTransaction tx, Guid organisationId) => Task.CompletedTask;
    }

    private sealed class PostgresDialect : SqlDialect
    {
        public override DatabaseProvider Provider => DatabaseProvider.Postgres;
        public override string MigrationFolder => "postgres";
        public override string ForUpdate => " FOR UPDATE";
        public override string ForUpdateSkipLocked => " FOR UPDATE SKIP LOCKED";
        public override object Id(Guid id) => id;
        public override Guid ReadId(DbDataReader reader, int ordinal) => reader.GetGuid(ordinal);

        public override async Task SetTenantContextAsync(DbConnection connection, DbTransaction tx, Guid organisationId)
        {
            // Transaction-local, so pooled connections never carry tenant context between requests.
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT set_config('app.organisation_id', @org, true)";
            cmd.Param("org", organisationId.ToString("D"));
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }
}
