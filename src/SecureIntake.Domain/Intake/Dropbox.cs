using SecureIntake.Domain.Common;

namespace SecureIntake.Domain.Intake;

/// <summary>Dropbox status (F6): <c>draft → active ⇄ paused → archived</c>.</summary>
public enum DropboxStatus
{
    Draft,
    Active,
    Paused,
    Archived,
}

/// <summary>Facts the activation check needs. Gathered by the application layer from other modules.</summary>
public sealed record DropboxActivationFacts(
    int DecryptingRecipientKeys,
    bool RecoveryRecipientRequired,
    bool HasRecoveryRecipient,
    bool HasPinnedForm,
    bool HasValidWorkflow,
    bool OkdRequired,
    bool HasCurrentOkdSnapshot);

public sealed class Dropbox
{
    public static readonly StateMachine<DropboxStatus> Lifecycle = new(
        "dropbox",
        (DropboxStatus.Draft, DropboxStatus.Active),
        (DropboxStatus.Draft, DropboxStatus.Archived),
        (DropboxStatus.Active, DropboxStatus.Paused),
        (DropboxStatus.Paused, DropboxStatus.Active),
        (DropboxStatus.Active, DropboxStatus.Archived),
        (DropboxStatus.Paused, DropboxStatus.Archived));

    public Dropbox(Guid id, Guid organisationId, string name, DropboxStatus status, long version)
    {
        Id = id;
        OrganisationId = organisationId;
        Name = name;
        Status = status;
        Version = version;
    }

    public Guid Id { get; }
    public Guid OrganisationId { get; }
    public string Name { get; }
    public DropboxStatus Status { get; private set; }
    public long Version { get; private set; }

    public bool AcceptsUploads => Status == DropboxStatus.Active;

    /// <summary>Returns the unmet activation gates (F6). Empty means the dropbox may be activated.</summary>
    public static IReadOnlyList<string> ActivationBlockers(DropboxActivationFacts f)
    {
        var blockers = new List<string>();
        if (f.DecryptingRecipientKeys < 1)
        {
            blockers.Add("dropbox.no_recipient_key");
        }
        if (f.RecoveryRecipientRequired && !f.HasRecoveryRecipient)
        {
            blockers.Add("dropbox.recovery_recipient_required");
        }
        if (!f.HasPinnedForm)
        {
            blockers.Add("dropbox.form_not_pinned");
        }
        if (!f.HasValidWorkflow)
        {
            blockers.Add("dropbox.workflow_invalid");
        }
        if (f.OkdRequired && !f.HasCurrentOkdSnapshot)
        {
            blockers.Add("dropbox.okd_snapshot_required");
        }
        return blockers;
    }

    public void Activate(DropboxActivationFacts facts)
    {
        Lifecycle.EnsureTransition(Status, DropboxStatus.Active);
        var blockers = ActivationBlockers(facts);
        if (blockers.Count > 0)
        {
            throw new DomainException("dropbox.activation_blocked", "Activation blocked: " + string.Join(", ", blockers));
        }
        Status = DropboxStatus.Active;
        Version++;
    }

    public void Pause() => Move(DropboxStatus.Paused);

    public void Archive() => Move(DropboxStatus.Archived);

    private void Move(DropboxStatus next)
    {
        Lifecycle.EnsureTransition(Status, next);
        Status = next;
        Version++;
    }
}
