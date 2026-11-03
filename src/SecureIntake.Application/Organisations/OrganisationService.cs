using SecureIntake.Application.Abstractions;
using SecureIntake.Application.Access;
using SecureIntake.Domain.Common;
using SecureIntake.Domain.Identifiers;
using SecureIntake.Domain.Organisations;

namespace SecureIntake.Application.Organisations;

public sealed record ProvisioningOptions(bool RequireApprovalForManual, bool RequireApprovalForApi, string DefaultDatabaseProfile, string DefaultStorageProfile);

/// <summary>
/// Unified provisioning pipeline (doc 02 §3, v4.5). The entry point sets the method and approval need;
/// storage and administration are the same for every method. Creating an organisation never lets it accept
/// uploads: it must pass activation first.
/// </summary>
public sealed class OrganisationService(IControlPlane controlPlane, Authorizer authorizer, ProvisioningOptions options, TimeProvider time)
{
    public Task<Organisation> CreateAsync(Principal actor, string displayName, string slug, ProvisioningMethod method, CancellationToken ct = default)
    {
        Require(actor, "platform.organisations.provision");
        if (method == ProvisioningMethod.SelfService)
        {
            throw new DomainException("organisation.self_service_disabled", "Self-service onboarding is not available until v1.3.");
        }
        var requiresApproval = method == ProvisioningMethod.Manual ? options.RequireApprovalForManual : options.RequireApprovalForApi;
        var org = Organisation.Create(displayName, slug, method, requiresApproval, time);

        return controlPlane.ExecuteAsync(async tx =>
        {
            if (await tx.FindOrganisationBySlugAsync(org.Slug).ConfigureAwait(false) is not null)
            {
                throw new DomainException("organisation.slug_taken", "That slug is already in use.");
            }
            await tx.InsertOrganisationAsync(org).ConfigureAwait(false);
            await tx.AppendAuditAsync(AuditEntry.Of("organisation.provisioning.requested", actor.AuditActor, Ext(org.Id),
                ("method", org.Method.ToString()), ("requiresApproval", requiresApproval))).ConfigureAwait(false);

            if (!requiresApproval)
            {
                var inserted = org.Version;
                await ProvisionSharedTierAsync(tx, actor, org).ConfigureAwait(false);
                await tx.UpdateOrganisationAsync(org, inserted).ConfigureAwait(false);
            }
            return org;
        }, ct);
    }

    public Task<Organisation> ApproveAsync(Principal actor, Guid id, CancellationToken ct = default)
    {
        Require(actor, "platform.organisations.approve");
        return Mutate(id, async (tx, org) =>
        {
            org.TransitionTo(OrganisationStatus.Provisioning);
            await tx.AppendAuditAsync(AuditEntry.Of("organisation.provisioning.approved", actor.AuditActor, Ext(org.Id))).ConfigureAwait(false);
            await ProvisionSharedTierAsync(tx, actor, org).ConfigureAwait(false);
        }, ct);
    }

    /// <summary>
    /// Activation gate (doc 02). In M1 the checks are: provisioned, routed and not fenced. Initial-admin and
    /// key gates are added with the Identity and Keys modules in M2.
    /// </summary>
    public Task<Organisation> ActivateAsync(Principal actor, Guid id, CancellationToken ct = default)
    {
        Require(actor, "platform.organisations.provision");
        return Mutate(id, async (tx, org) =>
        {
            var assignment = await tx.FindAssignmentAsync(org.Id).ConfigureAwait(false);
            if (assignment is null || assignment.WritesBlocked)
            {
                throw new DomainException("organisation.activation_blocked", "Organisation has no usable resource assignment.");
            }
            org.TransitionTo(OrganisationStatus.Active);
            await tx.AppendAuditAsync(AuditEntry.Of("organisation.activated", actor.AuditActor, Ext(org.Id),
                ("generation", assignment.Generation))).ConfigureAwait(false);
            await tx.EnqueueOutboxAsync(new OutboxEvent("organisation.activated", 1, org.Id, $"{{\"organisationId\":\"{Ext(org.Id)}\"}}")).ConfigureAwait(false);
        }, ct);
    }

    public Task<Organisation> SuspendAsync(Principal actor, Guid id, string reason, CancellationToken ct = default)
    {
        Require(actor, "platform.organisations.suspend");
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
        {
            throw new DomainException("organisation.reason_required", "A suspension reason of up to 500 characters is required.");
        }
        return Mutate(id, async (tx, org) =>
        {
            org.TransitionTo(OrganisationStatus.Suspended);
            await tx.AppendAuditAsync(AuditEntry.Of("organisation.suspended", actor.AuditActor, Ext(org.Id), ("reason", reason))).ConfigureAwait(false);
        }, ct);
    }

    public Task<Organisation> ReactivateAsync(Principal actor, Guid id, CancellationToken ct = default)
    {
        Require(actor, "platform.organisations.suspend");
        return Mutate(id, async (tx, org) =>
        {
            org.TransitionTo(OrganisationStatus.Active);
            await tx.AppendAuditAsync(AuditEntry.Of("organisation.reactivated", actor.AuditActor, Ext(org.Id))).ConfigureAwait(false);
        }, ct);
    }

    public Task<Organisation?> GetAsync(Principal actor, Guid id, CancellationToken ct = default)
    {
        Require(actor, "platform.organisations.view");
        return controlPlane.ExecuteAsync(tx => tx.FindOrganisationAsync(id), ct);
    }

    public Task<IReadOnlyList<Organisation>> ListAsync(Principal actor, int limit, CancellationToken ct = default)
    {
        Require(actor, "platform.organisations.view");
        return controlPlane.ExecuteAsync(tx => tx.ListOrganisationsAsync(Math.Clamp(limit, 1, 500)), ct);
    }

    private async Task ProvisionSharedTierAsync(IControlPlaneTransaction tx, Principal actor, Organisation org)
    {
        // v1.0 is shared-tier only; dedicated tiers arrive in v1.2 through the same assignment record.
        var assignment = new ResourceAssignment(org.Id, options.DefaultDatabaseProfile, options.DefaultStorageProfile, 1, false);
        await tx.UpsertAssignmentAsync(assignment).ConfigureAwait(false);
        await tx.AppendAuditAsync(AuditEntry.Of("organisation.resource_assigned", actor.AuditActor, Ext(org.Id),
            ("databaseProfile", assignment.DatabaseProfile), ("storageProfile", assignment.StorageProfile), ("generation", 1L))).ConfigureAwait(false);
        org.TransitionTo(OrganisationStatus.PendingActivation);
        await tx.AppendAuditAsync(AuditEntry.Of("organisation.provisioning.step_completed", actor.AuditActor, Ext(org.Id),
            ("step", "shared_tier_assigned"))).ConfigureAwait(false);
    }

    private Task<Organisation> Mutate(Guid id, Func<IControlPlaneTransaction, Organisation, Task> change, CancellationToken ct) =>
        controlPlane.ExecuteAsync(async tx =>
        {
            var org = await tx.FindOrganisationAsync(id).ConfigureAwait(false)
                ?? throw new DomainException("organisation.not_found", "Organisation not found.");
            var expected = org.Version;
            await change(tx, org).ConfigureAwait(false);
            await tx.UpdateOrganisationAsync(org, expected).ConfigureAwait(false);
            return org;
        }, ct);

    private void Require(Principal actor, string permission)
    {
        var result = authorizer.Evaluate(actor, permission);
        if (!result.IsAllowed)
        {
            throw new AuthorizationDeniedException(result);
        }
    }

    private static string Ext(Guid id) => new ExternalId(IdKind.Organisation, id).ToString();
}
