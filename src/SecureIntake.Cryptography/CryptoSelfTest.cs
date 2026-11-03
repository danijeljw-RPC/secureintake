using SecureIntake.Cryptography.E2ee;
using SecureIntake.Cryptography.Native;

namespace SecureIntake.Cryptography;

public sealed record CryptoSelfTestResult(bool LibsodiumAvailable, bool X25519KnownAnswer, bool MlKemAvailable, string? Error)
{
    /// <summary>True when every primitive SI-E2EE-1 needs works on this host.</summary>
    public bool SuiteUsable => LibsodiumAvailable && X25519KnownAnswer && MlKemAvailable;
}

/// <summary>Startup self-test (ADR-0002 requirement 4): native library present, RFC 7748 known answer, ML-KEM available.</summary>
public static class CryptoSelfTest
{
    public static CryptoSelfTestResult Run()
    {
        if (Sodium.AvailabilityError is { } error)
        {
            return new(false, false, HybridKem.IsSupported, error);
        }
        bool kat;
        try
        {
            var sk = Convert.FromHexString("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
            var peer = Convert.FromHexString("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f");
            kat = X25519.SharedSecret(sk, peer).AsSpan().SequenceEqual(
                Convert.FromHexString("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742"));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            kat = false;
        }
        return new(true, kat, HybridKem.IsSupported, kat ? null : "X25519 known-answer test failed.");
    }
}
