using SecureIntake.Contracts.Catalogue;

namespace SecureIntake.Application.Access;

public enum AuthorizationOutcome
{
    Allowed,
    Denied,
    /// <summary>The base role allows it, but a JIT elevation is needed first (Elevated or Sensitive level).</summary>
    ElevationRequired,
    /// <summary>Sensitive: elevation held, independent approval still needed.</summary>
    ApprovalRequired,
}

public sealed record AuthorizationResult(AuthorizationOutcome Outcome, string Permission, string? Reason = null)
{
    public bool IsAllowed => Outcome == AuthorizationOutcome.Allowed;
}

/// <summary>
/// RBAC evaluator over the generated catalogue (doc 22, doc 28). It never grants decryption: cryptographic
/// access always needs a recipient envelope. JIT elevation and approvals are modelled as inputs here;
/// the engines that issue them land in M3.
/// </summary>
public sealed class Authorizer(PermissionCatalogue catalogue)
{
    public AuthorizationResult Evaluate(Principal principal, string permission, Guid? organisationId = null, bool hasApproval = false)
    {
        if (!catalogue.Permissions.TryGetValue(permission, out var def))
        {
            return new(AuthorizationOutcome.Denied, permission, "unknown_permission");
        }
        if (principal.Kind == PrincipalKind.System)
        {
            return new(AuthorizationOutcome.Allowed, permission);
        }
        if (def.Level == "recovery")
        {
            return new(AuthorizationOutcome.Denied, permission, "recovery_procedure_only");
        }

        if (principal.Kind == PrincipalKind.ServiceAccount)
        {
            return EvaluateServiceAccount(principal, def, organisationId);
        }

        bool baseAllowed;
        if (def.Scope == "platform")
        {
            baseAllowed = principal.PlatformRoles.Any(r => catalogue.RoleGrants("platform", r, permission));
        }
        else
        {
            if (organisationId is not { } orgId || !principal.OrganisationRoles.TryGetValue(orgId, out var roles))
            {
                return new(AuthorizationOutcome.Denied, permission, "not_a_member");
            }
            baseAllowed = roles.Any(r => r != "api_client" && catalogue.RoleGrants("organisation", r, permission));
        }

        if (!baseAllowed)
        {
            return new(AuthorizationOutcome.Denied, permission, "role");
        }
        if (def.IsElevated && !principal.ActiveElevations.Contains(permission))
        {
            return new(AuthorizationOutcome.ElevationRequired, permission);
        }
        if (def.IsSensitive && !hasApproval)
        {
            return new(AuthorizationOutcome.ApprovalRequired, permission);
        }
        return new(AuthorizationOutcome.Allowed, permission);
    }

    /// <summary>
    /// ADR-0004: a service account never bypasses JIT. Anything above Standard is denied outright, whatever roles or
    /// permissions are stored. Only the Provisioning Client (platform) and API Client (organisation) roles count,
    /// and an API client also needs the permission in its explicit subset.
    /// </summary>
    private AuthorizationResult EvaluateServiceAccount(Principal principal, PermissionDefinition def, Guid? organisationId)
    {
        if (def.Level != "standard")
        {
            return new(AuthorizationOutcome.Denied, def.Name, "elevation_not_available_to_service_accounts");
        }
        bool allowed;
        if (def.Scope == "platform")
        {
            allowed = principal.PlatformRoles.Contains("provisioning_client") && catalogue.RoleGrants("platform", "provisioning_client", def.Name);
        }
        else
        {
            allowed = organisationId is { } org
                && principal.OrganisationRoles.TryGetValue(org, out var roles) && roles.Contains("api_client")
                && (catalogue.RoleGrants("organisation", "api_client", def.Name)
                    || (principal.ExplicitPermissions.Contains(def.Name) && ApiClientMayHold(def.Name)));
        }
        return allowed ? new(AuthorizationOutcome.Allowed, def.Name) : new(AuthorizationOutcome.Denied, def.Name, "role");
    }

    /// <summary>Whether an API client may be given <paramref name="permission"/>: Standard level and listed for the role.</summary>
    public bool ApiClientMayHold(string permission)
    {
        var role = catalogue.Role("organisation", "api_client");
        return role is not null && catalogue.Permissions.TryGetValue(permission, out var def) && def.Level == "standard"
            && (role.Eligible.Contains(permission) || role.Grants.Any(g => g.Permission == permission));
    }
}

public sealed class AuthorizationDeniedException(AuthorizationResult result)
    : Exception($"Permission '{result.Permission}' not granted: {result.Outcome}{(result.Reason is null ? "" : $" ({result.Reason})")}.")
{
    public AuthorizationResult Result { get; } = result;
}
