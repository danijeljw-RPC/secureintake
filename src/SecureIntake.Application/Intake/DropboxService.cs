using SecureIntake.Application.Abstractions;
using SecureIntake.Application.Access;
using SecureIntake.Domain.Common;
using SecureIntake.Domain.Identifiers;
using SecureIntake.Domain.Intake;

namespace SecureIntake.Application.Intake;

/// <summary>
/// Dropbox administration. M1 covers creation and listing (draft only); activation gates need the Keys
/// and Forms modules (M2). Every call is bound to one organisation's data plane.
/// </summary>
public sealed class DropboxService(IControlPlane controlPlane, ITenantDataPlane dataPlane, Authorizer authorizer, TimeProvider time)
{
    public async Task<Dropbox> CreateAsync(Principal actor, Guid organisationId, string name, CancellationToken ct = default)
    {
        Require(actor, "org.dropboxes.manage", organisationId);
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length is < 1 or > 120 || trimmed.Any(char.IsControl))
        {
            throw new DomainException("dropbox.invalid_name", "Name must be 1 to 120 printable characters.");
        }
        await EnsureOperatingAsync(organisationId, ct).ConfigureAwait(false);
        var dropbox = new Dropbox(Guid.CreateVersion7(time.GetUtcNow()), organisationId, trimmed, DropboxStatus.Draft, 1);
        return await dataPlane.ExecuteAsync(organisationId, async tx =>
        {
            await tx.InsertDropboxAsync(dropbox).ConfigureAwait(false);
            await tx.AppendAuditAsync(AuditEntry.Of("dropbox.created", actor.AuditActor,
                new ExternalId(IdKind.Dropbox, dropbox.Id).ToString(), ("name", dropbox.Name))).ConfigureAwait(false);
            return dropbox;
        }, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<Dropbox>> ListAsync(Principal actor, Guid organisationId, int limit, CancellationToken ct = default)
    {
        Require(actor, "org.dropboxes.view", organisationId);
        return dataPlane.ExecuteAsync(organisationId, tx => tx.ListDropboxesAsync(Math.Clamp(limit, 1, 500)), ct);
    }

    public Task<Dropbox?> GetAsync(Principal actor, Guid organisationId, Guid dropboxId, CancellationToken ct = default)
    {
        Require(actor, "org.dropboxes.view", organisationId);
        return dataPlane.ExecuteAsync(organisationId, tx => tx.FindDropboxAsync(dropboxId), ct);
    }

    private async Task EnsureOperatingAsync(Guid organisationId, CancellationToken ct)
    {
        var org = await controlPlane.ExecuteAsync(tx => tx.FindOrganisationAsync(organisationId), ct).ConfigureAwait(false);
        if (org is null || !org.CanOperate)
        {
            throw new DomainException("organisation.not_active", "The organisation is not active.");
        }
    }

    private void Require(Principal actor, string permission, Guid organisationId)
    {
        var result = authorizer.Evaluate(actor, permission, organisationId);
        if (!result.IsAllowed)
        {
            throw new AuthorizationDeniedException(result);
        }
    }
}
