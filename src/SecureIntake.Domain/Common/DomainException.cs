namespace SecureIntake.Domain.Common;

/// <summary>An invariant or state-machine rule was violated. Maps to 409 or 422 at the API edge.</summary>
public sealed class DomainException : Exception
{
    public DomainException(string code, string message) : base(message) => Code = code;

    public DomainException() : this("domain_error", "Domain rule violated.") { }

    public DomainException(string message) : this("domain_error", message) { }

    public DomainException(string message, Exception inner) : base(message, inner) => Code = "domain_error";

    /// <summary>Stable machine-readable code, for example <c>organisation.invalid_transition</c>.</summary>
    public string Code { get; }
}
