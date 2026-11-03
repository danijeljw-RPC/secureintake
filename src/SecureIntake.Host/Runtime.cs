using System.Security.Cryptography;
using SecureIntake.Application.Access;
using SecureIntake.Application.Intake;
using SecureIntake.Application.Organisations;
using SecureIntake.Contracts.Catalogue;
using SecureIntake.Cryptography.Signing;
using SecureIntake.Domain;
using SecureIntake.Infrastructure.Audit;
using SecureIntake.Infrastructure.Data;
using SecureIntake.Infrastructure.Identity;
using SecureIntake.Infrastructure.Jobs;
using SecureIntake.Infrastructure.Secrets;

namespace SecureIntake.Host;

/// <summary>Everything the host needs, built once from validated settings. Shared by `serve` and CLI verbs.</summary>
internal sealed class Runtime : IAsyncDisposable
{
    private Runtime(HostSettings settings, Database database, Es256Signer checkpointKey, byte[] pepper, ICheckpointSink sink, TimeProvider time)
    {
        Settings = settings;
        Database = database;
        CheckpointKey = checkpointKey;
        Time = time;
        Authorizer = new Authorizer(PermissionCatalogue.Current);
        ControlPlane = new ControlPlane(database, time);
        DataPlane = new TenantDataPlane(database, time);
        Organisations = new OrganisationService(ControlPlane, Authorizer,
            new ProvisioningOptions(settings.RequireApprovalManual, settings.RequireApprovalApi, "shared-primary", "shared-primary"), time);
        Dropboxes = new DropboxService(ControlPlane, DataPlane, Authorizer, time);
        ServiceAccounts = new ServiceAccountStore(database, pepper, time);
        Jobs = new JobStore(database, time);
        Sink = sink;
        Checkpoints = new CheckpointWriter(database, checkpointKey, sink, time);
        AuditVerifier = new AuditVerifier(database);
        Migrations = new MigrationRunner(database);
    }

    public HostSettings Settings { get; }
    public Database Database { get; }
    public Es256Signer CheckpointKey { get; }
    public TimeProvider Time { get; }
    public Authorizer Authorizer { get; }
    public ControlPlane ControlPlane { get; }
    public TenantDataPlane DataPlane { get; }
    public OrganisationService Organisations { get; }
    public DropboxService Dropboxes { get; }
    public ServiceAccountStore ServiceAccounts { get; }
    public JobStore Jobs { get; }
    public ICheckpointSink Sink { get; }
    public CheckpointWriter Checkpoints { get; }
    public AuditVerifier AuditVerifier { get; }
    public MigrationRunner Migrations { get; }

    public static Runtime Create(HostSettings s, bool forMigration = false, TimeProvider? time = null)
    {
        var dev = s.Profile == DeploymentProfile.Development;
        var secrets = new SecretResolver(allowLiteral: dev);
        Directory.CreateDirectory(s.DataDirectory);

        var database = s.DatabaseProvider == DatabaseProvider.Postgres
            ? Database.Postgres(System.Text.Encoding.UTF8.GetString(secrets.Resolve(
                forMigration && s.PostgresMigrationConnection is not null ? "Database:MigrationConnectionString" : "Database:ConnectionString",
                forMigration && s.PostgresMigrationConnection is not null ? s.PostgresMigrationConnection : s.PostgresConnection)))
            : Database.Sqlite(s.SqlitePath);

        var pepper = LoadPepper(secrets, s, dev);
        var key = SigningKeyProvider.LoadAuditCheckpointKey(secrets, s.AuditCheckpointKey, s.DataDirectory, allowGenerate: dev);
        ICheckpointSink sink = string.IsNullOrEmpty(s.CheckpointSinkPath) ? new NullSink() : new FileCheckpointSink(s.CheckpointSinkPath);
        return new Runtime(s, database, key, pepper, sink, time ?? TimeProvider.System);
    }

    /// <summary>Verifiers for checkpoint keys this deployment trusts. Rotation history arrives with key governance (M3).</summary>
    public Es256Verifier? VerifierFor(string keyId) => keyId == CheckpointKey.KeyId ? CheckpointKey.Verifier() : null;

    private static byte[] LoadPepper(SecretResolver secrets, HostSettings s, bool dev)
    {
        if (!string.IsNullOrEmpty(s.TokenPepper))
        {
            var raw = secrets.Resolve("Secrets:TokenPepper", s.TokenPepper);
            return raw.Length >= 32 ? raw : throw new SecretResolutionException("Secrets:TokenPepper", "must be at least 32 bytes");
        }
        if (!dev)
        {
            throw new SecretResolutionException("Secrets:TokenPepper", "is required");
        }
        var path = Path.Combine(s.DataDirectory, "keys", "token-pepper");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(32));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        SecretResolver.EnsureProtected("Secrets:TokenPepper", path);
        return File.ReadAllBytes(path);
    }

    public async ValueTask DisposeAsync()
    {
        CheckpointKey.Dispose();
        (Sink as IDisposable)?.Dispose();
        await Database.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class NullSink : ICheckpointSink
    {
        public Task WriteAsync(SecureIntake.Cryptography.Audit.AuditCheckpoint checkpoint, byte[] signature, CancellationToken ct) => Task.CompletedTask;
    }
}
