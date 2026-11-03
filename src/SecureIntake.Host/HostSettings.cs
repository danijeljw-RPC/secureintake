using System.Globalization;
using Microsoft.Extensions.Configuration;
using SecureIntake.Domain;
using SecureIntake.Infrastructure.Data;

namespace SecureIntake.Host;

/// <summary>
/// Typed host configuration (doc 19). Precedence, lowest first: built-in defaults → JSON file
/// (<c>--config</c> or <c>SECUREINTAKE_CONFIG</c>) → <c>SECUREINTAKE__Section__Key</c> environment variables →
/// <c>--Section:Key=value</c> arguments. Values that are secrets hold references (<c>env:</c>/<c>file:</c>).
/// </summary>
internal sealed record HostSettings
{
    public DeploymentProfile Profile { get; init; } = DeploymentProfile.Production;
    public string Environment { get; init; } = "production";
    public DatabaseProvider DatabaseProvider { get; init; } = DatabaseProvider.Sqlite;
    public string SqlitePath { get; init; } = "";
    public string? PostgresConnection { get; init; }
    public string? PostgresMigrationConnection { get; init; }
    public string DataDirectory { get; init; } = "";
    public string? TokenPepper { get; init; }
    public string? AuditCheckpointKey { get; init; }
    public string? CheckpointSinkPath { get; init; }
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromMinutes(15);
    public long? StorageBudgetBytes { get; init; }
    public IReadOnlyList<string> Urls { get; init; } = [];
    public bool TlsTerminatedUpstream { get; init; }
    public bool RequireApprovalManual { get; init; }
    public bool RequireApprovalApi { get; init; }
    public int WorkerConcurrency { get; init; } = Math.Min(32, 2 * System.Environment.ProcessorCount);
    public string ReferencePrefix { get; init; } = "SI";
    /// <summary>OS account names allowed to run privileged host CLI verbs (ADR-0004). Required outside Development.</summary>
    public IReadOnlyList<string> AuthorisedOperators { get; init; } = [];

    public static IConfiguration BuildConfiguration(CliArgs cli)
    {
        var builder = new ConfigurationBuilder();
        var file = cli.Get("config") ?? System.Environment.GetEnvironmentVariable("SECUREINTAKE_CONFIG");
        if (!string.IsNullOrEmpty(file))
        {
            builder.AddJsonFile(Path.GetFullPath(file), optional: false, reloadOnChange: false);
        }
        builder.AddEnvironmentVariables("SECUREINTAKE__");
        builder.AddInMemoryCollection(cli.ConfigOverrides);
        return builder.Build();
    }

    public static HostSettings From(IConfiguration c)
    {
        var profile = Enum.TryParse<DeploymentProfile>(c["Profile"], ignoreCase: true, out var p) ? p : DeploymentProfile.Production;
        var dataDir = Path.GetFullPath(c["DataDirectory"] ?? (profile == DeploymentProfile.Development ? "./.secureintake" : "/var/lib/secureintake"));
        var provider = (c["Database:Provider"] ?? "sqlite").ToLowerInvariant() switch
        {
            "postgres" or "postgresql" => DatabaseProvider.Postgres,
            "sqlite" => DatabaseProvider.Sqlite,
            var other => throw new InvalidOperationException($"Unknown Database:Provider '{other}'."),
        };
        return new HostSettings
        {
            Profile = profile,
            Environment = c["Environment"] ?? (profile == DeploymentProfile.Development ? "dev" : "production"),
            DatabaseProvider = provider,
            SqlitePath = c["Database:SqlitePath"] ?? Path.Combine(dataDir, "secureintake.db"),
            PostgresConnection = c["Database:ConnectionString"],
            PostgresMigrationConnection = c["Database:MigrationConnectionString"],
            DataDirectory = dataDir,
            TokenPepper = c["Secrets:TokenPepper"],
            AuditCheckpointKey = c["Signing:AuditCheckpointKey"],
            CheckpointSinkPath = c["Audit:CheckpointSinkPath"],
            CheckpointInterval = TimeSpan.FromSeconds(Long(c["Audit:CheckpointIntervalSeconds"]) ?? 900),
            StorageBudgetBytes = Long(c["Quotas:StorageBudgetBytes"]),
            Urls = (c["Http:Urls"] ?? (profile == DeploymentProfile.Development ? "http://127.0.0.1:8080" : "https://0.0.0.0:8443"))
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            TlsTerminatedUpstream = Bool(c["Http:TlsTerminatedUpstream"]),
            RequireApprovalManual = Bool(c["Onboarding:RequireApprovalForManual"]),
            RequireApprovalApi = Bool(c["Onboarding:RequireApprovalForApi"]),
            WorkerConcurrency = (int?)Long(c["Worker:Concurrency"]) ?? Math.Min(32, 2 * System.Environment.ProcessorCount),
            ReferencePrefix = c["Intake:ReferencePrefix"] ?? "SI",
            AuthorisedOperators = (c["Cli:AuthorisedOperators"] ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        };
    }

    private static long? Long(string? v) => long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static bool Bool(string? v) => bool.TryParse(v, out var b) && b;
}
