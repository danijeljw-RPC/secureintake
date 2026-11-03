using Microsoft.Extensions.Time.Testing;
using SecureIntake.Domain.Common;
using SecureIntake.Domain.Intake;
using SecureIntake.Domain.Organisations;

namespace SecureIntake.UnitTests;

public class OrganisationTests
{
    private static readonly FakeTimeProvider Time = new();

    [Fact]
    public void New_organisation_without_approval_starts_provisioning_and_cannot_operate()
    {
        var org = Organisation.Create("Acme Legal", "acme", ProvisioningMethod.Manual, requiresApproval: false, Time);
        Assert.Equal(OrganisationStatus.Provisioning, org.Status);
        Assert.False(org.CanOperate);
    }

    [Fact]
    public void Main_path_reaches_active()
    {
        var org = Organisation.Create("Acme Legal", "acme", ProvisioningMethod.Api, requiresApproval: true, Time);
        org.TransitionTo(OrganisationStatus.Provisioning);
        org.TransitionTo(OrganisationStatus.PendingActivation);
        org.TransitionTo(OrganisationStatus.Active);
        Assert.True(org.CanOperate);
        Assert.Equal(4, org.Version);
    }

    [Theory]
    [InlineData(OrganisationStatus.Provisioning, OrganisationStatus.Active)]
    [InlineData(OrganisationStatus.Rejected, OrganisationStatus.Provisioning)]
    [InlineData(OrganisationStatus.Deleted, OrganisationStatus.Active)]
    [InlineData(OrganisationStatus.PendingApproval, OrganisationStatus.Active)]
    public void Invalid_transitions_throw(OrganisationStatus from, OrganisationStatus to)
    {
        var org = new Organisation(Guid.CreateVersion7(), "Acme", "acme", ProvisioningMethod.Manual, from, 1, DateTimeOffset.UnixEpoch);
        var ex = Assert.Throws<DomainException>(() => org.TransitionTo(to));
        Assert.Equal("organisation.invalid_transition", ex.Code);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("-acme")]
    [InlineData("acme-")]
    [InlineData("ac--me")]
    [InlineData("Acme")]
    [InlineData("acme_legal")]
    public void Invalid_slugs_are_rejected(string slug) => Assert.Throws<DomainException>(() => Slugs.Validate(slug));
}

public class DropboxTests
{
    private static readonly DropboxActivationFacts Ready = new(1, true, true, true, true, false, false);

    [Fact]
    public void Draft_with_all_gates_met_activates()
    {
        var d = new Dropbox(Guid.CreateVersion7(), Guid.CreateVersion7(), "Intake", DropboxStatus.Draft, 1);
        d.Activate(Ready);
        Assert.True(d.AcceptsUploads);
    }

    [Fact]
    public void Each_unmet_gate_is_reported()
    {
        var blockers = Dropbox.ActivationBlockers(new(0, true, false, false, false, true, false));
        Assert.Equal(["dropbox.no_recipient_key", "dropbox.recovery_recipient_required", "dropbox.form_not_pinned",
            "dropbox.workflow_invalid", "dropbox.okd_snapshot_required"], blockers);
    }

    [Fact]
    public void Paused_and_archived_dropboxes_do_not_accept_uploads()
    {
        var d = new Dropbox(Guid.CreateVersion7(), Guid.CreateVersion7(), "Intake", DropboxStatus.Draft, 1);
        d.Activate(Ready);
        d.Pause();
        Assert.False(d.AcceptsUploads);
        d.Archive();
        Assert.Throws<DomainException>(() => d.Activate(Ready));
    }
}
