using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SecureIntake.Cryptography.E2ee;

/// <summary>
/// Content encryption for one file under SI-E2EE-1 (S1 §7). Key schedule, all HKDF-SHA-256 with salt = fileId(16):
/// <code>
/// contentKey(32) = HKDF(DEK, fileId, "SI-E2EE-1 content")
/// nonceBase(12)  = HKDF(DEK, fileId, "SI-E2EE-1 nonce")
/// commitKey(32)  = HKDF(DEK, fileId, "SI-E2EE-1 commit")
/// nonce_i = nonceBase XOR (u32be i ‖ u8 final ‖ 0x00×7)
/// AAD_i   = fileId(16) ‖ u32be i ‖ u8 final ‖ manifestDigest(32)        (53 bytes)
/// </code>
/// <c>final</c> is 1 exactly when <c>i = chunkCount − 1</c>, so each index has one nonce, and indexes give distinct
/// nonces. A fresh random DEK per file keeps (key, nonce) pairs unique across files.
/// </summary>
public sealed class ChunkCipher : IDisposable
{
    public const int AadSize = SiE2ee1.FileIdSize + 4 + 1 + SiE2ee1.DigestSize;

    private readonly AesGcm _aes;
    private readonly byte[] _nonceBase;
    private readonly byte[] _commitKey;
    private readonly FileHeader _header;

    public ChunkCipher(ReadOnlySpan<byte> dek, FileHeader header)
    {
        if (dek.Length != SiE2ee1.DekSize)
        {
            throw new CryptographicException("DEK must be 32 bytes.");
        }
        _header = header;
        var contentKey = Derive(dek, header.FileId, SiE2ee1.ContentInfo, SiE2ee1.KeySize);
        try
        {
            _aes = new AesGcm(contentKey, SiE2ee1.TagSize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contentKey);
        }
        _nonceBase = Derive(dek, header.FileId, SiE2ee1.NonceInfo, SiE2ee1.NonceSize);
        _commitKey = Derive(dek, header.FileId, SiE2ee1.CommitInfo, SiE2ee1.KeySize);
    }

    public FileHeader Header => _header;

    public static int CiphertextLength(int plaintextLength) => plaintextLength + SiE2ee1.TagSize;

    public byte[] EncryptChunk(uint index, ReadOnlySpan<byte> plaintext)
    {
        var expected = _header.ChunkPlaintextLength(index);
        if (plaintext.Length != expected)
        {
            throw new CryptographicException($"Chunk {index} must be {expected} bytes of plaintext.");
        }
        var output = new byte[CiphertextLength(plaintext.Length)];
        Span<byte> nonce = stackalloc byte[SiE2ee1.NonceSize];
        Span<byte> aad = stackalloc byte[AadSize];
        Build(index, nonce, aad);
        _aes.Encrypt(nonce, plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length), aad);
        return output;
    }

    /// <summary>Throws <see cref="AuthenticationTagMismatchException"/> on tampering, reordering, a wrong file or a wrong manifest.</summary>
    public byte[] DecryptChunk(uint index, ReadOnlySpan<byte> ciphertext)
    {
        var expected = _header.ChunkPlaintextLength(index);
        if (ciphertext.Length != CiphertextLength(expected))
        {
            throw new CryptographicException($"Chunk {index} ciphertext must be {CiphertextLength(expected)} bytes.");
        }
        var plaintext = new byte[expected];
        Span<byte> nonce = stackalloc byte[SiE2ee1.NonceSize];
        Span<byte> aad = stackalloc byte[AadSize];
        Build(index, nonce, aad);
        _aes.Decrypt(nonce, ciphertext[..expected], ciphertext[expected..], plaintext, aad);
        return plaintext;
    }

    /// <summary>
    /// Local resume commitment (S1 §7.4): <c>HMAC-SHA-256(commitKey, u32be i ‖ plaintext_i)</c>. A client must
    /// persist it before uploading chunk i, and on resume must re-check it before re-encrypting chunk i. A mismatch
    /// means the source changed, and the client must restart the file with a new fileId and DEK: re-encrypting
    /// different plaintext under the same nonce would break AES-GCM. Never sent to the server.
    /// </summary>
    public byte[] ResumeCommitment(uint index, ReadOnlySpan<byte> plaintext)
    {
        using var h = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, _commitKey);
        Span<byte> i = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(i, index);
        h.AppendData(i);
        h.AppendData(plaintext);
        return h.GetHashAndReset();
    }

    /// <summary>Exposed to tests and the vector generator.</summary>
    internal (byte[] Nonce, byte[] Aad) NonceAndAad(uint index)
    {
        var nonce = new byte[SiE2ee1.NonceSize];
        var aad = new byte[AadSize];
        Build(index, nonce, aad);
        return (nonce, aad);
    }

    internal static byte[] Derive(ReadOnlySpan<byte> ikm, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info, int length)
    {
        var output = new byte[length];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, output, salt, info);
        return output;
    }

    private void Build(uint index, Span<byte> nonce, Span<byte> aad)
    {
        if (index >= _header.ChunkCount)
        {
            throw new CryptographicException("Chunk index beyond the declared chunk count.");
        }
        var final = _header.IsFinal(index) ? (byte)1 : (byte)0;
        _nonceBase.CopyTo(nonce);
        Span<byte> mask = stackalloc byte[5];
        BinaryPrimitives.WriteUInt32BigEndian(mask, index);
        mask[4] = final;
        for (var i = 0; i < mask.Length; i++)
        {
            nonce[i] ^= mask[i];
        }

        _header.FileId.CopyTo(aad);
        BinaryPrimitives.WriteUInt32BigEndian(aad[SiE2ee1.FileIdSize..], index);
        aad[SiE2ee1.FileIdSize + 4] = final;
        _header.ManifestDigest.CopyTo(aad[(SiE2ee1.FileIdSize + 5)..]);
    }

    public void Dispose()
    {
        _aes.Dispose();
        CryptographicOperations.ZeroMemory(_nonceBase);
        CryptographicOperations.ZeroMemory(_commitKey);
    }
}
