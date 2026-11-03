using System.Text.Json;
using SecureIntake.Host;
using SecureIntake.Infrastructure.Data;

namespace SecureIntake.IntegrationTests;

/// <summary>ADR-0004: the host CLI enforces the same service-account rules as the API, and audits privileged verbs.</summary>
public sealed class CliTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("si-cli-");
    private readonly string _config;

    public CliTests()
    {
        _config = Path.Combine(_dir.FullName, "config.json");
        WriteConfig(null);
    }

    private void WriteConfig(string? operators) => File.WriteAllText(_config, JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["Profile"] = "Development",
        ["Environment"] = "test",
        ["DataDirectory"] = _dir.FullName,
        ["Cli"] = operators is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["AuthorisedOperators"] = operators },
    }));

    private async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await HostProgram.RunAsync([.. args, "--config", _config], o, e);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public async Task Cli_refuses_human_roles_and_elevated_permissions_for_service_accounts()
    {
        Assert.Equal(0, (await Run("migrate")).Code);
        var org = await Run("organisations", "create", "--name", "Acme", "--slug", "acme", "--activate");
        Assert.Equal(0, org.Code);
        var orgId = org.Out.Split(' ')[0];

        var owner = await Run("service-accounts", "create-platform", "--name", "x", "--roles", "owner");
        Assert.NotEqual(0, owner.Code);
        Assert.Contains("provisioning_client", owner.Err, StringComparison.Ordinal);

        var webhooks = await Run("service-accounts", "create-api-client", "--organisation", orgId, "--name", "x", "--permissions", "org.webhooks.manage");
        Assert.NotEqual(0, webhooks.Code);
        Assert.Contains("ADR-0004", webhooks.Err, StringComparison.Ordinal);

        Assert.Equal(0, (await Run("service-accounts", "create-api-client", "--organisation", orgId, "--name", "ok", "--permissions", "org.dropboxes.view,org.grants.issue_api")).Code);
    }

    [Fact]
    public async Task Audit_verify_reports_organisations_by_their_external_id()
    {
        Assert.Equal(0, (await Run("migrate")).Code);
        var org = await Run("organisations", "create", "--name", "Acme", "--slug", "acme", "--activate");
        var orgId = org.Out.Split(' ')[0];

        var verify = await Run("audit", "verify");
        Assert.Equal(0, verify.Code);
        Assert.Contains($"ok   {orgId} ", verify.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("org:", verify.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Serve_reports_a_port_clash_as_a_clear_error()
    {
        using var occupier = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        occupier.Start();
        var url = $"http://127.0.0.1:{((System.Net.IPEndPoint)occupier.LocalEndpoint).Port}";

        var serve = await Run("serve", $"--Http:Urls={url}");
        Assert.Equal(6, serve.Code);
        Assert.Contains("address already in use", serve.Err, StringComparison.Ordinal);
        Assert.Contains("Http:Urls", serve.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Privileged_verbs_are_audited_with_the_operator_identity()
    {
        Assert.Equal(0, (await Run("migrate")).Code);
        Assert.Equal(0, (await Run("organisations", "create", "--name", "Acme", "--slug", "acme")).Code);
        Assert.NotEqual(0, (await Run("organisations", "create", "--name", "Acme", "--slug", "acme")).Code); // duplicate slug

        await using var db = Database.Sqlite(Path.Combine(_dir.FullName, "secureintake.db"));
        await using var conn = await db.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT event, record_json FROM platform_audit ORDER BY seq";
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<(string Event, string Json)>();
        while (await r.ReadAsync())
        {
            rows.Add((r.GetString(0), r.GetString(1)));
        }
        var actor = $"system:host-cli:{Environment.UserName}@{Environment.MachineName}";
        var commands = rows.Where(x => x.Event == "host_cli.command").ToList();
        Assert.Equal(2, commands.Count);
        Assert.All(commands, c => Assert.Contains(actor, c.Json, StringComparison.Ordinal));
        Assert.Contains("\"outcome\":\"succeeded\"", commands[0].Json, StringComparison.Ordinal);
        Assert.Contains("\"error\":\"organisation.slug_taken\"", commands[1].Json, StringComparison.Ordinal);
        Assert.Contains(rows, x => x.Event == "organisation.provisioning.requested" && x.Json.Contains(actor, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Operator_outside_the_allowlist_is_refused()
    {
        WriteConfig("nobody-else");
        var r = await Run("organisations", "list");
        Assert.Equal(5, r.Code);
        Assert.Contains("Cli:AuthorisedOperators", r.Err, StringComparison.Ordinal);
        Assert.Equal(0, (await Run("version")).Code);
    }

    [Fact]
    public async Task Migrate_status_reports_pending_then_current()
    {
        var before = await Run("migrate", "status");
        Assert.Equal(4, before.Code);
        Assert.Contains("schema.uninitialised", before.Out, StringComparison.Ordinal);
        Assert.Equal(0, (await Run("migrate")).Code);
        var after = await Run("migrate", "status");
        Assert.Equal(0, after.Code);
        Assert.Contains("schema.current", after.Out, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { _dir.Delete(true); } catch (IOException) { }
    }
}
