using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SecureIntake.Api;
using SecureIntake.Application.Abstractions;
using SecureIntake.Application.Access;
using SecureIntake.Cryptography;
using SecureIntake.Domain;
using SecureIntake.Domain.Common;
using SecureIntake.Domain.Identifiers;
using SecureIntake.Domain.Organisations;
using SecureIntake.Infrastructure.Data;
using SecureIntake.Infrastructure.Identity;
using SecureIntake.Infrastructure.Secrets;
using SecureIntake.Worker;

namespace SecureIntake.Host;

internal static partial class HostProgram
{
    public static string Version => typeof(HostProgram).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static async Task<int> RunAsync(string[] args, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        var @out = stdout ?? Console.Out;
        var err = stderr ?? Console.Error;
        CliArgs cli;
        HostSettings settings;
        try
        {
            cli = CliArgs.Parse(args);
            if (cli.Verbs.Count == 0 || cli.Verb(0) is "help" || cli.Flag("help"))
            {
                PrintHelp(@out);
                return cli.Verbs.Count == 0 && !cli.Flag("help") ? 2 : 0;
            }
            if (cli.Verb(0) == "version")
            {
                await @out.WriteLineAsync($"secureintake {Version}").ConfigureAwait(false);
                return 0;
            }
            settings = HostSettings.From(HostSettings.BuildConfiguration(cli));
        }
        catch (Exception ex) when (ex is CliException or InvalidOperationException or FormatException)
        {
            await err.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            return 2;
        }

        var crypto = CryptoSelfTest.Run();
        var (errors, warnings) = ConfigValidator.Validate(settings, crypto);
        if (cli.Verb(0) == "migrate")
        {
            errors = [.. errors, .. ConfigValidator.ValidateMigrate(settings)];
        }
        if (cli.Verb(0) == "config" && cli.Verb(1) == "validate")
        {
            foreach (var w in warnings) await @out.WriteLineAsync($"warning: {w}").ConfigureAwait(false);
            foreach (var e in errors) await @out.WriteLineAsync($"error: {e}").ConfigureAwait(false);
            await @out.WriteLineAsync(errors.Count == 0 ? "configuration valid" : "configuration invalid").ConfigureAwait(false);
            return errors.Count == 0 ? 0 : 1;
        }
        if (errors.Count > 0)
        {
            foreach (var e in errors) await err.WriteLineAsync($"error: {e}").ConfigureAwait(false);
            await err.WriteLineAsync("Refusing to start. Run `secureintake config validate` for details.").ConfigureAwait(false);
            return 1;
        }
        if (HostOperator.Authorise(settings, cli.Verb(0)) is { } refusal)
        {
            await err.WriteLineAsync($"error: {refusal}").ConfigureAwait(false);
            return 5;
        }

        var ctx = new CliContext(cli, settings, crypto, @out, err);
        try
        {
            return cli.Verb(0) switch
            {
                "serve" => await ServeAsync(ctx, warnings).ConfigureAwait(false),
                "migrate" => await MigrateAsync(ctx).ConfigureAwait(false),
                "organisations" => await AuditedAsync(ctx, OrganisationsAsync).ConfigureAwait(false),
                "service-accounts" => await AuditedAsync(ctx, ServiceAccountsAsync).ConfigureAwait(false),
                "audit" => await AuditedAsync(ctx, AuditAsync).ConfigureAwait(false),
                _ => throw new CliException($"Unknown verb '{cli.Verb(0)}'. Run `secureintake help`."),
            };
        }
        catch (CliException ex)
        {
            await err.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            return 2;
        }
        catch (Exception ex) when (ex is DomainException or AuthorizationDeniedException or SecretResolutionException
                                       or ArgumentException or MigrationLockedException or InvalidOperationException)
        {
            await err.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    private sealed record CliContext(CliArgs Cli, HostSettings Settings, CryptoSelfTestResult Crypto, TextWriter Out, TextWriter Err)
    {
        public Principal Actor { get; } = Principal.HostOperator(HostOperator.Identity);
    }

    /// <summary>
    /// Runs a privileged verb and records a <c>host_cli.command</c> platform audit event with the operator,
    /// verb and outcome (ADR-0004). Business events inside the verb carry the same operator identity.
    /// </summary>
    private static async Task<int> AuditedAsync(CliContext ctx, Func<CliContext, Runtime, Task<int>> verb)
    {
        await using var runtime = Runtime.Create(ctx.Settings);
        string outcome = "failed";
        string? errorCode = null;
        try
        {
            var code = await verb(ctx, runtime).ConfigureAwait(false);
            outcome = code == 0 ? "succeeded" : "failed";
            return code;
        }
        catch (Exception ex)
        {
            errorCode = ex is DomainException d ? d.Code : ex.GetType().Name;
            throw;
        }
        finally
        {
            try
            {
                await runtime.ControlPlane.ExecuteAsync(async tx =>
                {
                    await tx.AppendAuditAsync(AuditEntry.Of("host_cli.command", ctx.Actor.AuditActor, null,
                        ("verb", ctx.Cli.Verb(0)), ("subverb", ctx.Cli.Verb(1)), ("outcome", outcome), ("error", errorCode))).ConfigureAwait(false);
                    return 0;
                }).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
            {
                await ctx.Err.WriteLineAsync("warning: the command ran but its host_cli.command audit record could not be written.").ConfigureAwait(false);
            }
        }
    }

    // ── serve ────────────────────────────────────────────────────────────────

    private static async Task<int> ServeAsync(CliContext ctx, IReadOnlyList<string> warnings)
    {
        var role = ctx.Cli.Get("role") ?? "all";
        if (role is not ("all" or "api" or "worker"))
        {
            throw new CliException("--role must be all, api or worker.");
        }
        await using var runtime = Runtime.Create(ctx.Settings);
        var app = BuildApp(runtime, role, warnings, crypto: ctx.Crypto);
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SecureIntake.Startup");

        // ADR-0007: only Development migrates implicitly. Every other profile reports Not Ready instead.
        if (ctx.Settings.Profile == DeploymentProfile.Development)
        {
            var applied = await runtime.Migrations.ApplyAsync("auto-migrate:development:" + HostOperator.Identity).ConfigureAwait(false);
            if (applied.Count > 0)
            {
                LogAutoMigrated(log, string.Join(", ", applied));
            }
        }
        var schema = await runtime.Migrations.StatusAsync().ConfigureAwait(false);
        if (!schema.IsCompatible)
        {
            LogSchemaNotReady(log, schema.ReasonCode, string.Join(",", schema.Pending), string.Join(",", schema.Unknown), string.Join(",", schema.Mismatched));
        }
        if (schema.InterruptedAttempts.Count > 0)
        {
            LogInterrupted(log, string.Join(",", schema.InterruptedAttempts));
        }
        if (schema.State != SchemaState.Unreachable)
        {
            var findings = await RuntimeRoleCheck.CheckAsync(runtime.Database).ConfigureAwait(false);
            if (findings.Count > 0)
            {
                LogPrivilegedRole(log, string.Join(' ', findings));
            }
        }
        await app.RunAsync().ConfigureAwait(false);
        return 0;
    }

    internal static WebApplication BuildApp(Runtime runtime, string role, IReadOnlyList<string> warnings,
        Action<WebApplicationBuilder>? configure = null, CryptoSelfTestResult? crypto = null)
    {
        var settings = runtime.Settings;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.UseUrls([.. settings.Urls]);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 1024 * 1024; // File content goes direct to storage, never through the API.
        });
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default));
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true);

        var s = builder.Services;
        s.AddSingleton(runtime.Time);
        s.AddSingleton(runtime.Authorizer);
        s.AddSingleton(runtime.Organisations);
        s.AddSingleton(runtime.Dropboxes);
        s.AddSingleton(runtime.Jobs);
        s.AddSingleton<IBearerAuthenticator>(runtime.ServiceAccounts);
        s.AddSingleton<IHealthReporter>(new HealthReporter(runtime, crypto ?? CryptoSelfTest.Run()));
        s.AddSingleton(new HostInfo(Version, settings.Profile.ToString(), role, warnings));

        if (role is "all" or "worker")
        {
            s.AddSingleton(runtime.Checkpoints);
            s.AddSingleton<IJobHandler, AuditCheckpointJob>();
            s.AddSingleton(new WorkerOptions(settings.WorkerConcurrency, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(1)));
            s.AddHostedService<JobDispatcher>();
            s.AddHostedService(sp => new AuditCheckpointScheduler(runtime.Jobs, runtime.Time, settings.CheckpointInterval));
        }
        configure?.Invoke(builder);

        var app = builder.Build();
        app.UseSecureIntakeApi();
        if (settings.Profile == DeploymentProfile.Development)
        {
            app.Use((ctx, next) =>
            {
                ctx.Response.Headers["SecureIntake-Banner"] = "development; pre-review-crypto";
                return next(ctx);
            });
        }
        app.MapHealth();
        if (role is "all" or "api")
        {
            app.MapPlatformOrganisations();
            app.MapOrganisationDropboxes();
        }
        app.MapFallback((HttpContext ctx) => ApiPipeline.WriteProblem(ctx, 404, "route.not_found", "Not found", null));
        return app;
    }

    // ── migrate ──────────────────────────────────────────────────────────────

    private static async Task<int> MigrateAsync(CliContext ctx)
    {
        await using var runtime = Runtime.Create(ctx.Settings, forMigration: true);
        if (ctx.Cli.Verb(1) == "status")
        {
            var st = await runtime.Migrations.StatusAsync().ConfigureAwait(false);
            await ctx.Out.WriteLineAsync($"state:       {st.ReasonCode}").ConfigureAwait(false);
            await ctx.Out.WriteLineAsync($"pending:     {Join(st.Pending)}").ConfigureAwait(false);
            await ctx.Out.WriteLineAsync($"unknown:     {Join(st.Unknown)}").ConfigureAwait(false);
            await ctx.Out.WriteLineAsync($"mismatched:  {Join(st.Mismatched)}").ConfigureAwait(false);
            await ctx.Out.WriteLineAsync($"interrupted: {Join(st.InterruptedAttempts)}").ConfigureAwait(false);
            return st.IsCompatible ? 0 : 4;
        }
        if (ctx.Cli.Verbs.Count > 1)
        {
            throw new CliException("Usage: secureintake migrate [status]");
        }
        var applied = await runtime.Migrations.ApplyAsync(HostOperator.Identity).ConfigureAwait(false);
        await ctx.Out.WriteLineAsync(applied.Count == 0 ? "schema is current" : $"applied: {string.Join(", ", applied)}").ConfigureAwait(false);
        return 0;

        static string Join(IReadOnlyList<int> v) => v.Count == 0 ? "-" : string.Join(", ", v.Select(x => x.ToString("D4", CultureInfo.InvariantCulture)));
    }

    // ── organisations ────────────────────────────────────────────────────────

    private static async Task<int> OrganisationsAsync(CliContext ctx, Runtime runtime)
    {
        var cli = ctx.Cli;
        switch (cli.Verb(1))
        {
            case "create":
            {
                var org = await runtime.Organisations.CreateAsync(ctx.Actor, cli.Require("name"), cli.Require("slug"), ProvisioningMethod.Manual).ConfigureAwait(false);
                if (cli.Flag("activate"))
                {
                    org = await runtime.Organisations.ActivateAsync(ctx.Actor, org.Id).ConfigureAwait(false);
                }
                await PrintOrg(ctx.Out, org).ConfigureAwait(false);
                return 0;
            }
            case "list":
                foreach (var org in await runtime.Organisations.ListAsync(ctx.Actor, 500).ConfigureAwait(false))
                {
                    await PrintOrg(ctx.Out, org).ConfigureAwait(false);
                }
                return 0;
            case "activate":
                await PrintOrg(ctx.Out, await runtime.Organisations.ActivateAsync(ctx.Actor, ParseOrg(cli.Require("organisation"))).ConfigureAwait(false)).ConfigureAwait(false);
                return 0;
            case "suspend":
                await PrintOrg(ctx.Out, await runtime.Organisations.SuspendAsync(ctx.Actor, ParseOrg(cli.Require("organisation")), cli.Require("reason")).ConfigureAwait(false)).ConfigureAwait(false);
                return 0;
            default:
                throw new CliException("Usage: secureintake organisations create|list|activate|suspend …");
        }
    }

    // ── service-accounts ─────────────────────────────────────────────────────

    /// <summary>Uses the same <see cref="ServiceAccountStore"/> rules as every other path (ADR-0004); the CLI adds none of its own.</summary>
    private static async Task<int> ServiceAccountsAsync(CliContext ctx, Runtime runtime)
    {
        var cli = ctx.Cli;
        switch (cli.Verb(1))
        {
            case "create-platform":
            {
                var roles = cli.Require("roles").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var created = await runtime.ServiceAccounts.CreatePlatformAsync(cli.Require("name"), roles, ctx.Actor.AuditActor).ConfigureAwait(false);
                await PrintToken(ctx.Out, created.Id, created.Token).ConfigureAwait(false);
                return 0;
            }
            case "create-api-client":
            {
                var perms = cli.Require("permissions").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var created = await runtime.ServiceAccounts.CreateOrganisationApiClientAsync(ParseOrg(cli.Require("organisation")),
                    cli.Require("name"), perms, runtime.Authorizer, ctx.Actor.AuditActor).ConfigureAwait(false);
                await PrintToken(ctx.Out, created.Id, created.Token).ConfigureAwait(false);
                return 0;
            }
            default:
                throw new CliException("Usage: secureintake service-accounts create-platform|create-api-client …");
        }
    }

    // ── audit ────────────────────────────────────────────────────────────────

    private static async Task<int> AuditAsync(CliContext ctx, Runtime runtime)
    {
        switch (ctx.Cli.Verb(1))
        {
            case "checkpoint":
                await ctx.Out.WriteLineAsync($"checkpoints written: {await runtime.Checkpoints.CheckpointAllAsync().ConfigureAwait(false)}").ConfigureAwait(false);
                return 0;
            case "verify":
            {
                var results = new List<SecureIntake.Infrastructure.Audit.AuditVerificationResult>
                {
                    await runtime.AuditVerifier.VerifyPlatformAsync(runtime.VerifierFor).ConfigureAwait(false),
                };
                var orgs = ctx.Cli.Get("organisation") is { } one
                    ? [ParseOrg(one)]
                    : (await runtime.Organisations.ListAsync(ctx.Actor, 500).ConfigureAwait(false)).Select(o => o.Id).ToList();
                foreach (var org in orgs)
                {
                    results.Add(await runtime.AuditVerifier.VerifyOrganisationAsync(org, runtime.VerifierFor).ConfigureAwait(false));
                }
                foreach (var r in results)
                {
                    await ctx.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                        $"{(r.IsValid ? "ok  " : "FAIL")} {r.Scope} records={r.Records} checkpoints={r.CheckpointsVerified}{(r.CheckpointsFailed > 0 ? $" failed_checkpoints={r.CheckpointsFailed}" : "")}{(r.FirstBadSeq is { } b ? $" first_bad_seq={b}" : "")}")).ConfigureAwait(false);
                }
                return results.All(r => r.IsValid) ? 0 : 3;
            }
            default:
                throw new CliException("Usage: secureintake audit verify|checkpoint");
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static Guid ParseOrg(string id) =>
        ExternalId.TryParse(id, IdKind.Organisation, out var parsed) ? parsed.Value.Value : throw new CliException($"'{id}' is not an organisation ID (org_…).");

    private static Task PrintOrg(TextWriter w, Organisation o) =>
        w.WriteLineAsync($"{new ExternalId(IdKind.Organisation, o.Id)}  {o.Status,-18} {o.Slug,-24} {o.DisplayName}");

    private static async Task PrintToken(TextWriter w, Guid id, string token)
    {
        await w.WriteLineAsync($"id:    {new ExternalId(IdKind.ServiceAccount, id)}").ConfigureAwait(false);
        await w.WriteLineAsync($"token: {token}").ConfigureAwait(false);
        await w.WriteLineAsync("The token is shown once and cannot be recovered. Store it in your secret manager.").ConfigureAwait(false);
    }

    private static void PrintHelp(TextWriter w) => w.WriteLine("""
        secureintake — Secure Intake host

        Usage:
          secureintake serve [--role all|api|worker]
          secureintake migrate [status]
          secureintake config validate
          secureintake organisations create --name <name> --slug <slug> [--activate]
          secureintake organisations list | activate --organisation <org_…> | suspend --organisation <org_…> --reason <text>
          secureintake service-accounts create-platform --name <name> --roles provisioning_client
          secureintake service-accounts create-api-client --organisation <org_…> --name <name> --permissions <p[,p]>
          secureintake audit verify [--organisation <org_…>] | checkpoint
          secureintake version

        Privileged verbs (migrate, organisations, service-accounts, audit) need the OS account to be listed in
        Cli:AuthorisedOperators outside Development, and are recorded in the platform audit trail.

        Configuration: --config <file.json> or SECUREINTAKE_CONFIG, SECUREINTAKE__Section__Key environment
        variables, and --Section:Key=value overrides (highest precedence).
        """);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Development auto-migration applied: {Migrations}")]
    private static partial void LogAutoMigrated(ILogger logger, string migrations);

    [LoggerMessage(Level = LogLevel.Error, Message = "Not ready: {Reason}. Pending={Pending} Unknown={Unknown} Mismatched={Mismatched}. Run `secureintake migrate status`; apply with `secureintake migrate` using the migration connection.")]
    private static partial void LogSchemaNotReady(ILogger logger, string reason, string pending, string unknown, string mismatched);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Interrupted migration attempts found for versions {Versions}; they were rolled back and can be re-run with `secureintake migrate`.")]
    private static partial void LogInterrupted(ILogger logger, string versions);

    [LoggerMessage(Level = LogLevel.Error, Message = "Runtime database role is over-privileged: {Findings}. It must not own tables, create in the schema or bypass RLS (ADR-0005, ADR-0007).")]
    private static partial void LogPrivilegedRole(ILogger logger, string findings);
}
