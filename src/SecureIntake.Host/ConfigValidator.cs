using SecureIntake.Cryptography;
using SecureIntake.Cryptography.E2ee;
using SecureIntake.Domain;
using SecureIntake.Infrastructure.Data;
using SecureIntake.Infrastructure.Secrets;

namespace SecureIntake.Host;

/// <summary>
/// Startup validation (F13, doc 26 §1, doc 27). Any error refuses startup; warnings are shown on the status
/// endpoint and in the Development banner.
/// </summary>
internal static class ConfigValidator
{
    public static (IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings) Validate(HostSettings s, CryptoSelfTestResult? crypto = null)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var strict = s.Profile != DeploymentProfile.Development;

        if (s.Profile == DeploymentProfile.Development)
        {
            if (string.Equals(s.Environment, "production", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("The Development profile refuses to start when Environment is 'production'.");
            }
            warnings.Add("Development profile: not for real data.");
        }

        if (!SiE2ee1.Reviewed)
        {
            if (strict)
            {
                errors.Add($"Encryption suite {SiE2ee1.SuiteId} has not passed external review; {s.Profile} refuses to start (doc 26 §1).");
            }
            else
            {
                warnings.Add($"Pre-review crypto: {SiE2ee1.SuiteId} has not passed external review.");
            }
        }

        if (crypto is not null && !crypto.SuiteUsable)
        {
            var problem = crypto.Error ?? (crypto.MlKemAvailable ? "crypto self-test failed" : "ML-KEM-768 unavailable (Linux needs OpenSSL 3.5+)");
            (strict ? errors : warnings).Add($"Cryptographic self-test: {problem}.");
        }

        if (strict)
        {
            if (s.AuthorisedOperators.Count == 0)
            {
                warnings.Add("Cli:AuthorisedOperators is empty, so privileged host CLI verbs will be refused.");
            }
            if (s.StorageBudgetBytes is null or <= 0)
            {
                errors.Add("Quotas:StorageBudgetBytes is required outside Development (doc 27).");
            }
            if (!s.TlsTerminatedUpstream && s.Urls.Any(u => !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add("Http:Urls must be https:// unless Http:TlsTerminatedUpstream is true.");
            }
            RequireReference(errors, "Secrets:TokenPepper", s.TokenPepper);
            RequireReference(errors, "Signing:AuditCheckpointKey", s.AuditCheckpointKey);
            if (s.DatabaseProvider == DatabaseProvider.Postgres)
            {
                RequireReference(errors, "Database:ConnectionString", s.PostgresConnection);
            }
        }

        if (s.DatabaseProvider == DatabaseProvider.Postgres && string.IsNullOrEmpty(s.PostgresConnection))
        {
            errors.Add("Database:ConnectionString is required for PostgreSQL.");
        }

        if (s.Profile == DeploymentProfile.Hardened && string.IsNullOrEmpty(s.CheckpointSinkPath))
        {
            errors.Add("Audit:CheckpointSinkPath is mandatory under the Hardened profile.");
        }
        else if (string.IsNullOrEmpty(s.CheckpointSinkPath))
        {
            warnings.Add("No external audit checkpoint sink configured; checkpoints are only stored in the database.");
        }

        if (s.WorkerConcurrency is < 1 or > 32)
        {
            errors.Add("Worker:Concurrency must be 1 to 32.");
        }
        return (errors, warnings);
    }

    /// <summary>Extra rules for <c>secureintake migrate</c> (ADR-0007).</summary>
    public static IReadOnlyList<string> ValidateMigrate(HostSettings s)
    {
        var errors = new List<string>();
        if (s.Profile != DeploymentProfile.Development && s.DatabaseProvider == DatabaseProvider.Postgres)
        {
            if (string.IsNullOrEmpty(s.PostgresMigrationConnection))
            {
                errors.Add("Database:MigrationConnectionString is required: migrations must use the schema owner, not the runtime role.");
            }
            else if (s.PostgresMigrationConnection == s.PostgresConnection)
            {
                errors.Add("Database:MigrationConnectionString must differ from Database:ConnectionString.");
            }
            else if (!SecretResolver.IsReference(s.PostgresMigrationConnection))
            {
                errors.Add("Database:MigrationConnectionString must be an env: or file: reference, not a literal value.");
            }
        }
        return errors;
    }

    private static void RequireReference(List<string> errors, string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            errors.Add($"{key} is required.");
        }
        else if (!SecretResolver.IsReference(value))
        {
            errors.Add($"{key} must be an env: or file: reference, not a literal value.");
        }
    }
}
