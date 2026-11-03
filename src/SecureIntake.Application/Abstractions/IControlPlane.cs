using SecureIntake.Domain.Organisations;

namespace SecureIntake.Application.Abstractions;

/// <summary>Shared control plane (doc 03 §2). Every write runs in one transaction with its platform audit record.</summary>
public interface IControlPlane
{
    Task<T> ExecuteAsync<T>(Func<IControlPlaneTransaction, Task<T>> work, CancellationToken ct = default);
}

public interface IControlPlaneTransaction
{
    Task<Organisation?> FindOrganisationAsync(Guid id);
    Task<Organisation?> FindOrganisationBySlugAsync(string slug);
    Task<IReadOnlyList<Organisation>> ListOrganisationsAsync(int limit);
    Task InsertOrganisationAsync(Organisation organisation);
    /// <summary>Optimistic update. Throws <see cref="ConcurrencyException"/> if the stored version isn't <paramref name="expectedVersion"/>.</summary>
    Task UpdateOrganisationAsync(Organisation organisation, long expectedVersion);

    Task<ResourceAssignment?> FindAssignmentAsync(Guid organisationId);
    Task UpsertAssignmentAsync(ResourceAssignment assignment);

    Task AppendAuditAsync(AuditEntry entry);
    Task EnqueueOutboxAsync(OutboxEvent evt);
}

/// <summary>Tenant routing record (<c>organisation_resource_assignments</c>). <see cref="Generation"/> is the fencing token.</summary>
public sealed record ResourceAssignment(Guid OrganisationId, string DatabaseProfile, string StorageProfile, long Generation, bool WritesBlocked);

public sealed class ConcurrencyException : Exception
{
    public ConcurrencyException() : base("The record was changed by another operation.") { }
    public ConcurrencyException(string message) : base(message) { }
    public ConcurrencyException(string message, Exception inner) : base(message, inner) { }
}
