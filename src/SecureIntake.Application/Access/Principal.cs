namespace SecureIntake.Application.Access;

public enum PrincipalKind
{
    User,
    ServiceAccount,
    System,
}

/// <summary>
/// An authenticated caller. Roles come from the authoritative store on every request.
/// <see cref="ExplicitPermissions"/> applies to API clients only, whose permissions are never implicit (doc 28).
/// </summary>
public sealed record Principal(
    Guid Id,
    PrincipalKind Kind,
    string DisplayId,
    IReadOnlySet<string> PlatformRoles,
    IReadOnlyDictionary<Guid, IReadOnlySet<string>> OrganisationRoles,
    IReadOnlySet<string> ExplicitPermissions,
    IReadOnlySet<string> ActiveElevations)
{
    public static readonly Principal System = SystemNamed("system");

    /// <summary>
    /// An authorised operator running host CLI verbs (ADR-0004). The identity, for example <c>alice@host-1</c>,
    /// is recorded on every audit record the command produces.
    /// </summary>
    public static Principal HostOperator(string operatorIdentity) => SystemNamed("host-cli:" + operatorIdentity);

    private static Principal SystemNamed(string name) => new(Guid.Empty, PrincipalKind.System, name,
        new HashSet<string>(), new Dictionary<Guid, IReadOnlySet<string>>(), new HashSet<string>(), new HashSet<string>());

    public string AuditActor => Kind switch
    {
        PrincipalKind.System => "system:" + DisplayId,
        _ => DisplayId,
    };
}
