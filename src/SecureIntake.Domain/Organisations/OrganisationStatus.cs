namespace SecureIntake.Domain.Organisations;

/// <summary>Organisation lifecycle (doc 02 §2). Separate from onboarding task progress.</summary>
public enum OrganisationStatus
{
    Requested,
    PendingApproval,
    Provisioning,
    ProvisioningFailed,
    PendingActivation,
    Active,
    Suspended,
    Rejected,
    DeletionPending,
    Deleted,
}

public enum ProvisioningMethod
{
    Manual,
    Api,
    SelfService,
}
