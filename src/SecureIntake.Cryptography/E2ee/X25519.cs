using System.Security.Cryptography;
using SecureIntake.Cryptography.Native;

namespace SecureIntake.Cryptography.E2ee;

/// <summary>
/// RFC 7748 X25519 via libsodium (ADR-0002). Internal: private keys and raw shared secrets never cross the
/// assembly boundary. Every native return code is checked; an all-zero shared secret (low-order or
/// non-canonical peer key that maps to the identity) is rejected.
/// </summary>
internal static class X25519
{
    public const int KeySize = 32;

    public static (byte[] PrivateKey, byte[] PublicKey) GenerateKeyPair()
    {
        var sk = RandomNumberGenerator.GetBytes(KeySize);
        return (sk, PublicKeyFrom(sk));
    }

    public static byte[] PublicKeyFrom(ReadOnlySpan<byte> privateKey)
    {
        Sodium.EnsureInitialised();
        ValidateLength(privateKey);
        var pk = new byte[KeySize];
        if (Sodium.crypto_scalarmult_curve25519_base(pk, privateKey) != 0)
        {
            throw new CryptographicException("X25519 base multiplication failed.");
        }
        return pk;
    }

    public static byte[] SharedSecret(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> peerPublicKey)
    {
        Sodium.EnsureInitialised();
        ValidateLength(privateKey);
        ValidateLength(peerPublicKey);
        var ss = new byte[KeySize];
        if (Sodium.crypto_scalarmult_curve25519(ss, privateKey, peerPublicKey) != 0)
        {
            CryptographicOperations.ZeroMemory(ss);
            throw new CryptographicException("X25519 rejected the peer public key.");
        }
        return ss;
    }

    private static void ValidateLength(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize)
        {
            throw new CryptographicException("X25519 keys are 32 bytes.");
        }
    }
}
