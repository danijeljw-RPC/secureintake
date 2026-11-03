namespace SecureIntake.Contracts.Organisations;

public sealed record CreateDropboxRequest(string Name);

public sealed record DropboxResponse(string Id, string OrganisationId, string Name, string Status, long Version);

public sealed record DropboxListResponse(IReadOnlyList<DropboxResponse> Items);

public sealed record SuspendOrganisationRequest(string Reason);

/// <summary>RFC 9457 problem details plus a stable machine-readable <see cref="Code"/>.</summary>
public sealed record ProblemResponse(string Type, string Title, int Status, string Code, string? Detail);
