using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using SecureIntake.Application.Access;
using SecureIntake.Domain;
using SecureIntake.Domain.Identifiers;
using SecureIntake.Host;

namespace SecureIntake.IntegrationTests;

public sealed class HttpTests : IAsyncLifetime
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("si-http-");
    private Runtime _runtime = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        var settings = new HostSettings
        {
            Profile = DeploymentProfile.Development,
            Environment = "test",
            DataDirectory = _dir.FullName,
            SqlitePath = Path.Combine(_dir.FullName, "si.db"),
            Urls = ["http://127.0.0.1:0"],
        };
        _runtime = Runtime.Create(settings);
        await _runtime.Migrations.ApplyAsync("test-operator");
        _app = HostProgram.BuildApp(_runtime, "api", [], b => b.WebHost.UseTestServer());
        await _app.StartAsync();
        _http = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        await _runtime.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { _dir.Delete(true); } catch (IOException) { }
    }

    private static HttpRequestMessage Req(HttpMethod m, string url, string? token, object? body = null)
    {
        var r = new HttpRequestMessage(m, url);
        if (token is not null)
        {
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        if (body is not null)
        {
            r.Content = JsonContent.Create(body);
        }
        return r;
    }

    private static async Task<string> Code(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString()!;

    [Fact]
    public async Task Liveness_is_public_and_carries_security_headers()
    {
        var r = await _http.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("2026-11", r.Headers.GetValues("SecureIntake-Api-Version").Single());
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task Api_requires_authentication_and_rejects_bad_tokens()
    {
        var anon = await _http.GetAsync("/api/platform/organisations");
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
        Assert.Equal("application/problem+json", anon.Content.Headers.ContentType!.MediaType);
        var bad = await _http.SendAsync(Req(HttpMethod.Get, "/api/platform/organisations", "sit_nope"));
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        Assert.Equal("auth.invalid_token", await Code(bad));
    }

    [Fact]
    public async Task Unsupported_api_version_is_rejected()
    {
        var r = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        r.Headers.Add("SecureIntake-Api-Version", "1999-01");
        var resp = await _http.SendAsync(r);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("api.unsupported_version", await Code(resp));
    }

    [Fact]
    public async Task Readiness_is_minimal_and_status_needs_dashboard_permission()
    {
        var ready = await _http.GetAsync("/health/ready");
        Assert.Equal("{\"status\":\"ready\"}", await ready.Content.ReadAsStringAsync());
        var prov = await _runtime.ServiceAccounts.CreatePlatformAsync("p", ["provisioning_client"], "system:test");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("/health/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.SendAsync(Req(HttpMethod.Get, "/health/status", prov.Token))).StatusCode);
    }

    /// <summary>ADR-0004: the Provisioning Client keeps Standard view access but can't provision, approve or activate.</summary>
    [Fact]
    public async Task Provisioning_client_cannot_use_elevated_operations()
    {
        var prov = await _runtime.ServiceAccounts.CreatePlatformAsync("provisioner", ["provisioning_client"], "system:test");
        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(Req(HttpMethod.Get, "/api/platform/organisations", prov.Token))).StatusCode);

        var create = await _http.SendAsync(Req(HttpMethod.Post, "/api/platform/organisations", prov.Token, new { displayName = "Tenant", slug = "tenant-x" }));
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal("access.denied", await Code(create));

        var org = await _runtime.Organisations.CreateAsync(Principal.System, "Pending", "pending-org", SecureIntake.Domain.Organisations.ProvisioningMethod.Manual);
        var id = new ExternalId(IdKind.Organisation, org.Id).ToString();
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.SendAsync(Req(HttpMethod.Post, $"/api/platform/organisations/{id}/activate", prov.Token))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.SendAsync(Req(HttpMethod.Post, $"/api/platform/organisations/{id}/approve", prov.Token))).StatusCode);
    }

    [Fact]
    public async Task Api_client_is_confined_to_its_tenant_and_standard_permissions()
    {
        var org = await _runtime.Organisations.CreateAsync(Principal.System, "Swoovr Test Tenant", "swoovr-test", SecureIntake.Domain.Organisations.ProvisioningMethod.Manual);
        await _runtime.Organisations.ActivateAsync(Principal.System, org.Id);
        var orgId = new ExternalId(IdKind.Organisation, org.Id).ToString();
        var other = await _runtime.Organisations.CreateAsync(Principal.System, "Other", "other-org", SecureIntake.Domain.Organisations.ProvisioningMethod.Manual);
        await _runtime.Organisations.ActivateAsync(Principal.System, other.Id);
        var otherId = new ExternalId(IdKind.Organisation, other.Id).ToString();

        var api = await _runtime.ServiceAccounts.CreateOrganisationApiClientAsync(org.Id, "integrator",
            ["org.dropboxes.view", "org.dropboxes.manage"], _runtime.Authorizer, "system:test");

        var dbx = await _http.SendAsync(Req(HttpMethod.Post, $"/api/organisations/{orgId}/dropboxes", api.Token, new { name = "Claims" }));
        Assert.Equal(HttpStatusCode.Created, dbx.StatusCode);
        var dbxId = JsonDocument.Parse(await dbx.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;

        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(Req(HttpMethod.Get, $"/api/organisations/{orgId}/dropboxes/{dbxId}", api.Token))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.SendAsync(Req(HttpMethod.Get, $"/api/organisations/{otherId}/dropboxes", api.Token))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.SendAsync(Req(HttpMethod.Get, "/api/platform/organisations", api.Token))).StatusCode);

        var invalid = await _http.SendAsync(Req(HttpMethod.Post, $"/api/organisations/{orgId}/dropboxes", api.Token, new { name = "" }));
        Assert.Equal((HttpStatusCode)422, invalid.StatusCode);
        Assert.Equal("dropbox.invalid_name", await Code(invalid));
    }

    [Fact]
    public async Task Wrong_kind_or_malformed_ids_are_404()
    {
        var prov = await _runtime.ServiceAccounts.CreatePlatformAsync("p", ["provisioning_client"], "system:test");
        var wrongKind = ExternalId.New(IdKind.Dropbox).ToString();
        Assert.Equal(HttpStatusCode.NotFound, (await _http.SendAsync(Req(HttpMethod.Get, $"/api/platform/organisations/{wrongKind}", prov.Token))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.SendAsync(Req(HttpMethod.Get, "/api/platform/organisations/org_garbage", prov.Token))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.SendAsync(Req(HttpMethod.Get, "/nowhere", prov.Token))).StatusCode);
    }
}

public class ConfigValidatorTests
{
    private static HostSettings Dev => new() { Profile = DeploymentProfile.Development, Environment = "dev", Urls = ["http://127.0.0.1:8080"] };

    [Fact]
    public void Development_starts_with_warnings()
    {
        var (errors, warnings) = ConfigValidator.Validate(Dev);
        Assert.Empty(errors);
        Assert.Contains(warnings, w => w.Contains("Pre-review crypto", StringComparison.Ordinal));
    }

    [Fact]
    public void Development_refuses_production_environment() =>
        Assert.NotEmpty(ConfigValidator.Validate(Dev with { Environment = "production" }).Errors);

    [Fact]
    public void Production_refuses_unreviewed_crypto_and_literal_secrets()
    {
        var (errors, _) = ConfigValidator.Validate(new HostSettings
        {
            Profile = DeploymentProfile.Production,
            StorageBudgetBytes = 1L << 40,
            Urls = ["https://0.0.0.0:8443"],
            TokenPepper = "literal-secret",
            AuditCheckpointKey = "file:/etc/secureintake/audit.p8.b64",
        });
        Assert.Contains(errors, e => e.Contains("external review", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Secrets:TokenPepper must be an env: or file: reference", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, e => e.Contains("AuditCheckpointKey", StringComparison.Ordinal));
    }

    [Fact]
    public void Production_requires_storage_budget_and_https()
    {
        var (errors, _) = ConfigValidator.Validate(new HostSettings { Profile = DeploymentProfile.Production, Urls = ["http://0.0.0.0:80"] });
        Assert.Contains(errors, e => e.Contains("StorageBudgetBytes", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("https://", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_ml_kem_blocks_production_but_only_warns_in_development()
    {
        var noMlKem = new SecureIntake.Cryptography.CryptoSelfTestResult(true, true, false, null);
        Assert.Contains(ConfigValidator.Validate(new HostSettings { Profile = DeploymentProfile.Production }, noMlKem).Errors,
            e => e.Contains("ML-KEM", StringComparison.Ordinal));
        var (errors, warnings) = ConfigValidator.Validate(Dev, noMlKem);
        Assert.Empty(errors);
        Assert.Contains(warnings, w => w.Contains("ML-KEM", StringComparison.Ordinal));
    }

    [Fact]
    public void Production_postgres_migrations_need_a_separate_owner_connection()
    {
        var pg = new HostSettings
        {
            Profile = DeploymentProfile.Production,
            DatabaseProvider = SecureIntake.Infrastructure.Data.DatabaseProvider.Postgres,
            PostgresConnection = "env:SI_DB",
        };
        Assert.NotEmpty(ConfigValidator.ValidateMigrate(pg));
        Assert.NotEmpty(ConfigValidator.ValidateMigrate(pg with { PostgresMigrationConnection = "env:SI_DB" }));
        Assert.NotEmpty(ConfigValidator.ValidateMigrate(pg with { PostgresMigrationConnection = "Host=x;Password=literal" }));
        Assert.Empty(ConfigValidator.ValidateMigrate(pg with { PostgresMigrationConnection = "env:SI_DB_OWNER" }));
        Assert.Empty(ConfigValidator.ValidateMigrate(Dev));
    }

    [Fact]
    public void Privileged_cli_verbs_need_an_operator_allowlist_outside_development()
    {
        var prod = new HostSettings { Profile = DeploymentProfile.Production };
        Assert.NotNull(HostOperator.Authorise(prod, "organisations"));
        Assert.NotNull(HostOperator.Authorise(prod with { AuthorisedOperators = ["someone-else"] }, "migrate"));
        Assert.Null(HostOperator.Authorise(prod with { AuthorisedOperators = [Environment.UserName] }, "migrate"));
        Assert.Null(HostOperator.Authorise(prod, "version"));
        Assert.Null(HostOperator.Authorise(Dev, "organisations"));
        Assert.NotNull(HostOperator.Authorise(Dev with { AuthorisedOperators = ["someone-else"] }, "organisations"));
    }

    [Fact]
    public void Hardened_requires_external_checkpoint_sink() =>
        Assert.Contains(ConfigValidator.Validate(new HostSettings { Profile = DeploymentProfile.Hardened }).Errors,
            e => e.Contains("CheckpointSinkPath", StringComparison.Ordinal));
}
