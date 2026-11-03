using SecureIntake.Application.Abstractions;
using SecureIntake.Cryptography;
using SecureIntake.Cryptography.E2ee;
using SecureIntake.Domain;
using SecureIntake.Infrastructure.Data;

namespace SecureIntake.Host;

/// <summary>
/// Readiness = schema exactly current, plus (outside Development) a non-privileged runtime role and a usable
/// crypto suite. Never migrates or repairs anything (ADR-0007).
/// </summary>
internal sealed class HealthReporter(Runtime runtime, CryptoSelfTestResult crypto) : IHealthReporter
{
    private bool Strict => runtime.Settings.Profile != DeploymentProfile.Development;

    public async Task<bool> IsReadyAsync(CancellationToken ct = default)
    {
        var components = await ComponentsAsync(ct).ConfigureAwait(false);
        return components.All(c => c.Status != "unhealthy");
    }

    public async Task<IReadOnlyList<ComponentHealth>> ComponentsAsync(CancellationToken ct = default)
    {
        var list = new List<ComponentHealth>();
        var schema = await runtime.Migrations.StatusAsync(ct).ConfigureAwait(false);
        list.Add(new("database", schema.State == SchemaState.Unreachable ? "unhealthy" : "healthy",
            schema.State == SchemaState.Unreachable ? "database.unreachable" : null));
        if (schema.State != SchemaState.Unreachable)
        {
            list.Add(new("schema", schema.IsCompatible ? "healthy" : "unhealthy", schema.IsCompatible ? null : schema.ReasonCode));

            IReadOnlyList<string> findings;
            try
            {
                findings = await RuntimeRoleCheck.CheckAsync(runtime.Database, ct).ConfigureAwait(false);
            }
            catch (System.Data.Common.DbException)
            {
                findings = ["runtime_role.unverified"];
            }
            list.Add(new("runtimeRole", findings.Count == 0 ? "healthy" : Strict ? "unhealthy" : "degraded",
                findings.Count == 0 ? null : string.Join(' ', findings)));

            try
            {
                var counts = await runtime.Jobs.CountsByStateAsync(ct).ConfigureAwait(false);
                list.Add(new("jobs", counts.GetValueOrDefault("dead") > 0 ? "degraded" : "healthy",
                    counts.GetValueOrDefault("dead") > 0 ? "jobs.dead_present" : null));
            }
            catch (System.Data.Common.DbException)
            {
                list.Add(new("jobs", "degraded", "jobs.unavailable"));
            }
        }
        list.Add(new("cryptoSelfTest", crypto.SuiteUsable ? "healthy" : Strict ? "unhealthy" : "degraded",
            crypto.SuiteUsable ? null : !crypto.LibsodiumAvailable ? "crypto.libsodium_unavailable" : !crypto.X25519KnownAnswer ? "crypto.x25519_kat_failed" : "crypto.mlkem_unavailable"));
        list.Add(new("encryptionSuite", SiE2ee1.Reviewed ? "healthy" : "degraded", SiE2ee1.Reviewed ? null : "crypto.suite_pre_review"));
        list.Add(new("auditCheckpointSink", string.IsNullOrEmpty(runtime.Settings.CheckpointSinkPath) ? "degraded" : "healthy",
            string.IsNullOrEmpty(runtime.Settings.CheckpointSinkPath) ? "audit.no_external_sink" : null));
        return list;
    }
}
