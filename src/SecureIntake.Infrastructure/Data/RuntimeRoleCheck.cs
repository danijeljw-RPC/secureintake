namespace SecureIntake.Infrastructure.Data;

/// <summary>
/// Verifies that the runtime database credentials can't modify the schema or bypass RLS (ADR-0005, ADR-0007).
/// PostgreSQL only; SQLite has no roles. Returns stable finding codes, never connection details.
/// </summary>
public static class RuntimeRoleCheck
{
    public static async Task<IReadOnlyList<string>> CheckAsync(Database database, CancellationToken ct = default)
    {
        if (database.Dialect.Provider != DatabaseProvider.Postgres)
        {
            return [];
        }
        await using var conn = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.rolsuper, r.rolbypassrls, r.rolcreatedb, r.rolcreaterole,
                   has_schema_privilege(current_user, current_schema(), 'CREATE'),
                   EXISTS (SELECT 1 FROM pg_tables t WHERE t.schemaname = current_schema() AND t.tableowner = current_user)
            FROM pg_roles r WHERE r.rolname = current_user
            """;
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return ["runtime_role.unknown"];
        }
        var findings = new List<string>();
        if (r.GetBoolean(0)) findings.Add("runtime_role.superuser");
        if (r.GetBoolean(1)) findings.Add("runtime_role.bypass_rls");
        if (r.GetBoolean(2)) findings.Add("runtime_role.create_database");
        if (r.GetBoolean(3)) findings.Add("runtime_role.create_role");
        if (r.GetBoolean(4)) findings.Add("runtime_role.schema_create");
        if (r.GetBoolean(5)) findings.Add("runtime_role.owns_tables");
        return findings;
    }
}
