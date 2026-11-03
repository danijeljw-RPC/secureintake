namespace SecureIntake.Contracts.Organisations;

public sealed record CreateOrganisationRequest(string DisplayName, string Slug, string? InitialAdministratorEmail);

public sealed record OrganisationResponse(
    string Id, string DisplayName, string Slug, string Status, string ProvisioningMethod, long Version, DateTimeOffset CreatedAt);

public sealed record OrganisationListResponse(IReadOnlyList<OrganisationResponse> Items);
