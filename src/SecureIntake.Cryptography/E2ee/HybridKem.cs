using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SecureIntake.Cryptography.E2ee;

/// <summary>A recipient's SI-E2EE-1 public key: X25519 public key (32) plus ML-KEM-768 encapsulation key (1184).</summary>
public sealed class HybridPublicKey
{
    private readonly byte[] _x25519;
    private readonly byte[] _mlKem;

    public HybridPublicKey(ReadOnlySpan<byte> x25519, ReadOnlySpan<byte> mlKem768)
    {
        if (x25519.Length != SiE2ee1.X25519PublicKeySize || mlKem768.Length != SiE2ee1.MlKem768EncapsulationKeySize)
        {
            throw new CryptographicException("SI-E2EE-1 public keys are 32 + 1184 bytes.");
        }
        _x25519 = x25519.ToArray();
        _mlKem = mlKem768.ToArray();
    }

    public ReadOnlySpan<byte> X25519 => _x25519;
    public ReadOnlySpan<byte> MlKem768 => _mlKem;

    /// <summary>
    /// S1 §9: <c>SHA-256("SI-E2EE-1 recipient key" ‖ 0x00 ‖ u16be suiteCode ‖ x25519(32) ‖ mlkem768Ek(1184))</c>.
    /// Fixed lengths make the encoding unambiguous.
    /// </summary>
    public byte[] Fingerprint()
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(SiE2ee1.FingerprintLabel);
        Span<byte> prefix = stackalloc byte[3];
        prefix[0] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(prefix[1..], SiE2ee1.SuiteCode);
        h.AppendData(prefix);
        h.AppendData(_x25519);
        h.AppendData(_mlKem);
        return h.GetHashAndReset();
    }
}

/// <summary>A recipient's private key. Held only by reviewer clients, never by the server. No export API here.</summary>
public sealed class HybridPrivateKey : IDisposable
{
    private readonly byte[] _x25519;
    private readonly MLKem _mlKem;

    private HybridPrivateKey(byte[] x25519, MLKem mlKem)
    {
        _x25519 = x25519;
        _mlKem = mlKem;
        PublicKey = new HybridPublicKey(E2ee.X25519.PublicKeyFrom(x25519), mlKem.ExportEncapsulationKey());
    }

    public HybridPublicKey PublicKey { get; }

    public static HybridPrivateKey Generate()
    {
        HybridKem.EnsureSupported();
        return new HybridPrivateKey(RandomNumberGenerator.GetBytes(E2ee.X25519.KeySize), MLKem.GenerateKey(MLKemAlgorithm.MLKem768));
    }

    /// <summary>Rebuilds a key from its X25519 private key and the 64-byte ML-KEM seed (d ‖ z, FIPS 203). Copies its inputs.</summary>
    public static HybridPrivateKey FromSeeds(ReadOnlySpan<byte> x25519PrivateKey, ReadOnlySpan<byte> mlKemSeed)
    {
        HybridKem.EnsureSupported();
        if (x25519PrivateKey.Length != E2ee.X25519.KeySize || mlKemSeed.Length != SiE2ee1.MlKem768SeedSize)
        {
            throw new CryptographicException("Expected a 32-byte X25519 key and a 64-byte ML-KEM seed.");
        }
        return new HybridPrivateKey(x25519PrivateKey.ToArray(), MLKem.ImportPrivateSeed(MLKemAlgorithm.MLKem768, mlKemSeed));
    }

    internal byte[] X25519Agree(ReadOnlySpan<byte> peer) => E2ee.X25519.SharedSecret(_x25519, peer);

    internal byte[] MlKemDecapsulate(ReadOnlySpan<byte> ciphertext)
    {
        var ss = new byte[32];
        _mlKem.Decapsulate(ciphertext, ss);
        return ss;
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_x25519);
        _mlKem.Dispose();
    }
}

/// <summary>
/// Hybrid KEM for SI-E2EE-1 (S1 §5), provisional HKDF combiner:
/// <code>
/// ct = ct_X(32) ‖ ct_M(1088)          where ct_X is the ephemeral X25519 public key
/// ss = HKDF-SHA-256(ikm = ss_M(32) ‖ ss_X(32) ‖ ct_X(32) ‖ pk_X(32), salt = "", info = "SI-E2EE-1 kem", L = 32)
/// </code>
/// Internal: shared secrets never leave the assembly. Callers use <see cref="Envelope"/>.
/// </summary>
internal static class HybridKem
{
    public const int CiphertextSize = X25519.KeySize + SiE2ee1.MlKem768CiphertextSize;

    public static bool IsSupported => MLKem.IsSupported;

    internal static void EnsureSupported()
    {
        if (!MLKem.IsSupported)
        {
            throw new PlatformNotSupportedException("ML-KEM-768 is unavailable. Linux hosts need OpenSSL 3.5 or later.");
        }
    }

    public static (byte[] Ciphertext, byte[] SharedSecret) Encapsulate(HybridPublicKey recipient)
    {
        var ephemeral = RandomNumberGenerator.GetBytes(X25519.KeySize);
        try
        {
            return Encapsulate(recipient, ephemeral);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ephemeral);
        }
    }

    /// <summary>X25519 half deterministic for test vectors; ML-KEM encapsulation randomness comes from the platform.</summary>
    internal static (byte[] Ciphertext, byte[] SharedSecret) Encapsulate(HybridPublicKey recipient, ReadOnlySpan<byte> ephemeralPrivateKey)
    {
        EnsureSupported();
        var ephPk = X25519.PublicKeyFrom(ephemeralPrivateKey);
        var ssX = X25519.SharedSecret(ephemeralPrivateKey, recipient.X25519);
        using var mlKem = MLKem.ImportEncapsulationKey(MLKemAlgorithm.MLKem768, recipient.MlKem768);
        var ctM = new byte[SiE2ee1.MlKem768CiphertextSize];
        var ssM = new byte[32];
        mlKem.Encapsulate(ctM, ssM);
        try
        {
            return ([.. ephPk, .. ctM], Combine(ssM, ssX, ephPk, recipient.X25519));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ssX);
            CryptographicOperations.ZeroMemory(ssM);
        }
    }

    public static byte[] Decapsulate(HybridPrivateKey key, ReadOnlySpan<byte> ciphertext)
    {
        if (ciphertext.Length != CiphertextSize)
        {
            throw new CryptographicException("Hybrid KEM ciphertext has the wrong length.");
        }
        var ctX = ciphertext[..X25519.KeySize];
        var ssX = key.X25519Agree(ctX);
        var ssM = key.MlKemDecapsulate(ciphertext[X25519.KeySize..]);
        try
        {
            return Combine(ssM, ssX, ctX, key.PublicKey.X25519);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ssX);
            CryptographicOperations.ZeroMemory(ssM);
        }
    }

    public static byte[] Combine(ReadOnlySpan<byte> ssMlKem, ReadOnlySpan<byte> ssX25519, ReadOnlySpan<byte> ctX25519, ReadOnlySpan<byte> pkX25519)
    {
        if (ssMlKem.Length != 32 || ssX25519.Length != 32 || ctX25519.Length != 32 || pkX25519.Length != 32)
        {
            throw new CryptographicException("Combiner inputs are four 32-byte values.");
        }
        Span<byte> ikm = stackalloc byte[128];
        ssMlKem.CopyTo(ikm);
        ssX25519.CopyTo(ikm[32..]);
        ctX25519.CopyTo(ikm[64..]);
        pkX25519.CopyTo(ikm[96..]);
        try
        {
            var ss = new byte[32];
            HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, ss, ReadOnlySpan<byte>.Empty, SiE2ee1.KemInfo);
            return ss;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ikm);
        }
    }
}
