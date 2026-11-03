namespace SecureIntake.Host;

/// <summary>
/// The OS account running a host CLI verb. Privileged verbs (anything that reads or changes the database)
/// require the account to be in <c>Cli:AuthorisedOperators</c> outside Development (ADR-0004). This is an
/// accountability control layered on OS permissions to the config, secrets and data directory; it isn't a
/// substitute for them.
/// </summary>
internal static class HostOperator
{
    /// <summary><c>user@host</c>. On Unix, .NET resolves the user name from the effective UID, not from $USER.</summary>
    public static string Identity => $"{Environment.UserName}@{Environment.MachineName}";

    public static readonly IReadOnlySet<string> PrivilegedVerbs = new HashSet<string>(StringComparer.Ordinal)
    {
        "migrate", "organisations", "service-accounts", "audit",
    };

    /// <summary>Null when allowed; otherwise the refusal message.</summary>
    public static string? Authorise(HostSettings settings, string verb)
    {
        if (!PrivilegedVerbs.Contains(verb))
        {
            return null;
        }
        var allowed = settings.AuthorisedOperators;
        if (allowed.Count == 0)
        {
            return settings.Profile == Domain.DeploymentProfile.Development
                ? null
                : "Cli:AuthorisedOperators is empty. Privileged CLI verbs are refused outside Development until it lists the permitted OS accounts.";
        }
        return allowed.Contains(Environment.UserName, StringComparer.Ordinal)
            ? null
            : $"OS account '{Environment.UserName}' is not in Cli:AuthorisedOperators.";
    }
}
