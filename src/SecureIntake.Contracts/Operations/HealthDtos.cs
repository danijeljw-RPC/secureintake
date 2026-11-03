namespace SecureIntake.Contracts.Operations;

public sealed record HealthResponse(string Status);

/// <summary>Authorised detail (<c>platform.dashboard.view</c>). <see cref="Reasons"/> are stable codes, never credentials.</summary>
public sealed record HealthStatusResponse(
    string Status, string Version, string Profile, string Role, IReadOnlyDictionary<string, string> Components,
    IReadOnlyList<string> Reasons, IReadOnlyList<string> Warnings);
