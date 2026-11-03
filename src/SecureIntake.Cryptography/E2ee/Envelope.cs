using System.Security.Cryptography;

namespace SecureIntake.Cryptography.E2ee;

/// <summary>
/// One recipient's envelope for one file: hybrid KEM ciphertext (1120 bytes) plus the wrapped DEK (60 bytes).
/// <see cref="RecipientKeyId"/> is the recipient key record's UUIDv7; on the wire it's the 16 big-endian
/// RFC 9562 bytes, never the <c>key_…</c> text form.
/// </summary>
public sealed record Envelope(Guid RecipientKeyId, byte[] KemCiphertext, byte[] WrappedDek)
{
    public static Envelope Seal(HybridPublicKey recipient, Guid recipientKeyId, ReadOnlySpan<byte> dek, ReadOnlySpan<byte> fileId)
    {
        var (ct, ss) = HybridKem.Encapsulate(recipient);
        try
        {
            return new Envelope(recipientKeyId, ct, KeyWrap.Wrap(ss, dek, fileId, KeyIdBytes(recipientKeyId), recipient.Fingerprint()));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ss);
        }
    }

    /// <summary>Recovers the DEK. Fails if the key, file, key ID or any byte of the envelope is wrong.</summary>
    public byte[] Open(HybridPrivateKey key, ReadOnlySpan<byte> fileId)
    {
        var ss = HybridKem.Decapsulate(key, KemCiphertext);
        try
        {
            return KeyWrap.Unwrap(ss, WrappedDek, fileId, KeyIdBytes(RecipientKeyId), key.PublicKey.Fingerprint());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ss);
        }
    }

    public static byte[] KeyIdBytes(Guid id)
    {
        var b = new byte[16];
        id.TryWriteBytes(b, bigEndian: true, out _);
        return b;
    }
}
