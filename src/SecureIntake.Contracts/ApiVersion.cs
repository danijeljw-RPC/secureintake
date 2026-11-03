namespace SecureIntake.Contracts;

/// <summary>API compatibility is negotiated by header, never by URL prefix (P-15).</summary>
public static class ApiVersion
{
    public const string HeaderName = "SecureIntake-Api-Version";
    public const string Current = "2026-11";
    public static readonly IReadOnlyList<string> Supported = [Current];
}
