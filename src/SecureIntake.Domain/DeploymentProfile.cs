namespace SecureIntake.Domain;

/// <summary>Security baseline (F13). Separate from the environment label.</summary>
public enum DeploymentProfile
{
    Development,
    Production,
    Hardened,
}
