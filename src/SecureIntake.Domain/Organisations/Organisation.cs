using SecureIntake.Domain.Common;

namespace SecureIntake.Domain.Organisations;

public sealed class Organisation
{
    public static readonly StateMachine<OrganisationStatus> Lifecycle = new(
        "organisation",
        (OrganisationStatus.Requested, OrganisationStatus.PendingApproval),
        (OrganisationStatus.Requested, OrganisationStatus.Provisioning),
        (OrganisationStatus.Requested, OrganisationStatus.Rejected),
        (OrganisationStatus.PendingApproval, OrganisationStatus.Provisioning),
        (OrganisationStatus.PendingApproval, OrganisationStatus.Rejected),
        (OrganisationStatus.Provisioning, OrganisationStatus.PendingActivation),
        (OrganisationStatus.Provisioning, OrganisationStatus.ProvisioningFailed),
        (OrganisationStatus.ProvisioningFailed, OrganisationStatus.Provisioning),
        (OrganisationStatus.PendingActivation, OrganisationStatus.Active),
        (OrganisationStatus.Active, OrganisationStatus.Suspended),
        (OrganisationStatus.Suspended, OrganisationStatus.Active),
        (OrganisationStatus.Active, OrganisationStatus.DeletionPending),
        (OrganisationStatus.Suspended, OrganisationStatus.DeletionPending),
        (OrganisationStatus.DeletionPending, OrganisationStatus.Deleted));

    public Organisation(Guid id, string displayName, string slug, ProvisioningMethod method, OrganisationStatus status,
        long version, DateTimeOffset createdAt)
    {
        Id = id;
        DisplayName = ValidateDisplayName(displayName);
        Slug = Slugs.Validate(slug);
        Method = method;
        Status = status;
        Version = version;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }
    public string DisplayName { get; private set; }
    /// <summary>Internal handle only. Never part of a public route (P-13).</summary>
    public string Slug { get; }
    public ProvisioningMethod Method { get; }
    public OrganisationStatus Status { get; private set; }
    /// <summary>Optimistic-concurrency version, incremented on every persisted change.</summary>
    public long Version { get; private set; }
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Only an active organisation may accept uploads or issue grants.</summary>
    public bool CanOperate => Status == OrganisationStatus.Active;

    public static Organisation Create(string displayName, string slug, ProvisioningMethod method, bool requiresApproval, TimeProvider time) =>
        new(Guid.CreateVersion7(time.GetUtcNow()), displayName, slug, method,
            requiresApproval ? OrganisationStatus.PendingApproval : OrganisationStatus.Provisioning, 1, time.GetUtcNow());

    public OrganisationStatus TransitionTo(OrganisationStatus next)
    {
        Lifecycle.EnsureTransition(Status, next);
        var previous = Status;
        Status = next;
        Version++;
        return previous;
    }

    public void Rename(string displayName)
    {
        DisplayName = ValidateDisplayName(displayName);
        Version++;
    }

    private static string ValidateDisplayName(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is < 2 or > 120 || trimmed.Any(char.IsControl))
        {
            throw new DomainException("organisation.invalid_name", "Display name must be 2 to 120 printable characters.");
        }
        return trimmed;
    }
}

public static class Slugs
{
    public static string Validate(string slug)
    {
        if (string.IsNullOrEmpty(slug) || slug.Length is < 3 or > 48
            || !slug.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
            || slug[0] == '-' || slug[^1] == '-' || slug.Contains("--", StringComparison.Ordinal))
        {
            throw new DomainException("organisation.invalid_slug", "Slug must be 3 to 48 lowercase letters, digits or single hyphens.");
        }
        return slug;
    }
}
