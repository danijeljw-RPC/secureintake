using System.Data.Common;

namespace SecureIntake.Infrastructure.Data;

internal static class DbExtensions
{
    public static DbCommand Param(this DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
        return cmd;
    }

    public static DbCommand Command(this DbTransaction tx, string sql)
    {
        var cmd = tx.Connection!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    public static string? GetNullableString(this DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    public static long? GetNullableInt64(this DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

    public static byte[] GetBytes(this DbDataReader r, int i) => (byte[])r.GetValue(i);
}
