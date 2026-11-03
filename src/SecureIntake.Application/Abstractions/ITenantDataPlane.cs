using SecureIntake.Domain.Intake;

namespace SecureIntake.Application.Abstractions;

/// <summary>
/// Tenant data plane, resolved server-side from the routing registry. Every transaction is bound to one
/// organisation: the implementation sets the tenant context (RLS on PostgreSQL) and checks the
/// assignment generation so a fenced tenant can't be written.
/// </summary>
public interface ITenantDataPlane
{
    Task<T> ExecuteAsync<T>(Guid organisationId, Func<ITenantTransaction, Task<T>> work, CancellationToken ct = default);
}

public interface ITenantTransaction
{
    Guid OrganisationId { get; }
    long Generation { get; }

    Task<Dropbox?> FindDropboxAsync(Guid id);
    Task<IReadOnlyList<Dropbox>> ListDropboxesAsync(int limit);
    Task InsertDropboxAsync(Dropbox dropbox);
    Task UpdateDropboxAsync(Dropbox dropbox, long expectedVersion);

    Task AppendAuditAsync(AuditEntry entry);
    Task EnqueueOutboxAsync(OutboxEvent evt);
}

public sealed class TenantFencedException : Exception
{
    public TenantFencedException() : base("Tenant writes are fenced.") { }
    public TenantFencedException(string message) : base(message) { }
    public TenantFencedException(string message, Exception inner) : base(message, inner) { }
}
