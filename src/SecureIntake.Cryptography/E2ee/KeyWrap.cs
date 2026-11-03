using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SecureIntake.Cryptography.E2ee;

/// <summary>
/// DEK wrap for one recipient (S1 §8):
/// <code>
/// kek   = HKDF-SHA-256(ikm = kemSharedSecret, salt = fileId(16), info = "SI-E2EE-1 wrap", L = 32)
/// aad   = u16be suiteCode ‖ fileId(16) ‖ recipientKeyId(16) ‖ recipientFingerprint(32)      (66 bytes)
/// wrapped = nonce(12) ‖ AES-256-GCM(kek, nonce, DEK(32), aad) ‖ tag(16)                  (60 bytes)
/// </code>
/// The nonce comes from the platform CSPRNG. Each KEK comes from a fresh encapsulation and wraps exactly one
/// DEK once, so a (KEK, nonce) pair can't repeat. Internal so nothing outside <see cref="Envelope"/> can reuse a KEK.
/// </summary>
internal static class KeyWrap
{
    public const int WrappedSize = SiE2ee1.NonceSize + SiE2ee1.DekSize + SiE2ee1.TagSize;
    public const int AadSize = 2 + SiE2ee1.FileIdSize + SiE2ee1.KeyIdSize + SiE2ee1.DigestSize;

    public static byte[] Wrap(ReadOnlySpan<byte> kemSharedSecret, ReadOnlySpan<byte> dek, ReadOnlySpan<byte> fileId,
        ReadOnlySpan<byte> recipientKeyId, ReadOnlySpan<byte> recipientFingerprint)
    {
        Span<byte> nonce = stackalloc byte[SiE2ee1.NonceSize];
        RandomNumberGenerator.Fill(nonce);
        return Wrap(kemSharedSecret, dek, fileId, recipientKeyId, recipientFingerprint, nonce);
    }

    /// <summary>Deterministic form for test vectors only.</summary>
    internal static byte[] Wrap(ReadOnlySpan<byte> kemSharedSecret, ReadOnlySpan<byte> dek, ReadOnlySpan<byte> fileId,
        ReadOnlySpan<byte> recipientKeyId, ReadOnlySpan<byte> recipientFingerprint, ReadOnlySpan<byte> nonce)
    {
        if (dek.Length != SiE2ee1.DekSize || nonce.Length != SiE2ee1.NonceSize)
        {
            throw new CryptographicException("DEK must be 32 bytes and the nonce 12 bytes.");
        }
        var aad = Aad(fileId, recipientKeyId, recipientFingerprint);
        var kek = DeriveKek(kemSharedSecret, fileId);
        try
        {
            using var aes = new AesGcm(kek, SiE2ee1.TagSize);
            var output = new byte[WrappedSize];
            nonce.CopyTo(output);
            aes.Encrypt(nonce, dek, output.AsSpan(SiE2ee1.NonceSize, SiE2ee1.DekSize), output.AsSpan(SiE2ee1.NonceSize + SiE2ee1.DekSize), aad);
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    public static byte[] Unwrap(ReadOnlySpan<byte> kemSharedSecret, ReadOnlySpan<byte> wrapped, ReadOnlySpan<byte> fileId,
        ReadOnlySpan<byte> recipientKeyId, ReadOnlySpan<byte> recipientFingerprint)
    {
        if (wrapped.Length != WrappedSize)
        {
            throw new CryptographicException("Wrapped DEK has the wrong length.");
        }
        var aad = Aad(fileId, recipientKeyId, recipientFingerprint);
        var kek = DeriveKek(kemSharedSecret, fileId);
        try
        {
            using var aes = new AesGcm(kek, SiE2ee1.TagSize);
            var dek = new byte[SiE2ee1.DekSize];
            aes.Decrypt(wrapped[..SiE2ee1.NonceSize], wrapped.Slice(SiE2ee1.NonceSize, SiE2ee1.DekSize),
                wrapped[(SiE2ee1.NonceSize + SiE2ee1.DekSize)..], dek, aad);
            return dek;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    internal static byte[] DeriveKek(ReadOnlySpan<byte> ss, ReadOnlySpan<byte> fileId)
    {
        if (ss.Length != 32 || fileId.Length != SiE2ee1.FileIdSize)
        {
            throw new CryptographicException("KEM shared secret must be 32 bytes and file ID 16 bytes.");
        }
        return ChunkCipher.Derive(ss, fileId, SiE2ee1.WrapInfo, SiE2ee1.KeySize);
    }

    internal static byte[] Aad(ReadOnlySpan<byte> fileId, ReadOnlySpan<byte> recipientKeyId, ReadOnlySpan<byte> recipientFingerprint)
    {
        if (fileId.Length != SiE2ee1.FileIdSize || recipientKeyId.Length != SiE2ee1.KeyIdSize || recipientFingerprint.Length != SiE2ee1.DigestSize)
        {
            throw new CryptographicException("Wrap AAD needs a 16-byte file ID, 16-byte key ID and 32-byte fingerprint.");
        }
        var aad = new byte[AadSize];
        BinaryPrimitives.WriteUInt16BigEndian(aad, SiE2ee1.SuiteCode);
        fileId.CopyTo(aad.AsSpan(2));
        recipientKeyId.CopyTo(aad.AsSpan(18));
        recipientFingerprint.CopyTo(aad.AsSpan(34));
        return aad;
    }
}
