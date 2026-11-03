using System.Security.Cryptography;

namespace SecureIntake.Cryptography.Signing;

/// <summary>SI-SIG-1: ECDSA P-256 with SHA-256, signatures in IEEE P1363 (r ‖ s, 64 bytes) form as JWS requires.</summary>
public sealed class Es256Signer : IDisposable
{
    private readonly ECDsa _key;

    private Es256Signer(ECDsa key, string keyId)
    {
        _key = key;
        KeyId = keyId;
    }

    public string KeyId { get; }

    public static Es256Signer Generate(string keyId) => new(ECDsa.Create(ECCurve.NamedCurves.nistP256), keyId);

    public static Es256Signer FromPkcs8(ReadOnlySpan<byte> pkcs8, string keyId)
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8, out _);
        if (key.KeySize != 256)
        {
            key.Dispose();
            throw new CryptographicException("ES256 requires a P-256 key.");
        }
        return new Es256Signer(key, keyId);
    }

    public byte[] ExportPkcs8() => _key.ExportPkcs8PrivateKey();

    public byte[] ExportSubjectPublicKeyInfo() => _key.ExportSubjectPublicKeyInfo();

    public Es256Verifier Verifier() => Es256Verifier.FromSubjectPublicKeyInfo(ExportSubjectPublicKeyInfo());

    public byte[] Sign(ReadOnlySpan<byte> data) =>
        _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public void Dispose() => _key.Dispose();
}

public sealed class Es256Verifier : IDisposable
{
    private readonly ECDsa _key;

    private Es256Verifier(ECDsa key) => _key = key;

    public static Es256Verifier FromSubjectPublicKeyInfo(ReadOnlySpan<byte> spki)
    {
        var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(spki, out _);
        return new Es256Verifier(key);
    }

    /// <summary>SHA-256 over the SPKI DER: the key fingerprint used by trust registries and OKD pinning.</summary>
    public static byte[] Fingerprint(ReadOnlySpan<byte> spki) => SHA256.HashData(spki);

    public bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature) =>
        signature.Length == 64
        && _key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public void Dispose() => _key.Dispose();
}
