using SecureIntake.Application.Access;

namespace SecureIntake.Application.Abstractions;

/// <summary>Resolves a bearer credential to a principal from the authoritative store, or null.</summary>
public interface IBearerAuthenticator
{
    Task<Principal?> AuthenticateAsync(string? bearerToken, CancellationToken ct = default);
}

/// <summary><paramref name="Reason"/> is a stable, non-sensitive code such as <c>schema.migrations_pending</c>.</summary>
public sealed record ComponentHealth(string Name, string Status, string? Reason = null);

/// <summary>Readiness and status checks, implemented by the host (doc 21).</summary>
public interface IHealthReporter
{
    /// <summary>API: authoritative DB reachable and schema compatible. Worker: can safely claim jobs.</summary>
    Task<bool> IsReadyAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ComponentHealth>> ComponentsAsync(CancellationToken ct = default);
}
