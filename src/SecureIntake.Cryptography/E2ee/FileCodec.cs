using System.Security.Cryptography;

namespace SecureIntake.Cryptography.E2ee;

/// <summary>Whole-stream helpers over <see cref="ChunkCipher"/>, used by the CLI and by tests.</summary>
public static class FileCodec
{
    /// <summary>Encrypts exactly <c>header.PlaintextLength</c> bytes from <paramref name="plaintext"/>.</summary>
    public static async Task<List<byte[]>> EncryptAsync(Stream plaintext, ChunkCipher cipher, CancellationToken ct = default)
    {
        var header = cipher.Header;
        var chunks = new List<byte[]>((int)Math.Min(header.ChunkCount, 1024));
        var buffer = new byte[SiE2ee1.ChunkSize];
        try
        {
            for (uint i = 0; i < header.ChunkCount; i++)
            {
                var length = header.ChunkPlaintextLength(i);
                await plaintext.ReadExactlyAsync(buffer.AsMemory(0, length), ct).ConfigureAwait(false);
                chunks.Add(cipher.EncryptChunk(i, buffer.AsSpan(0, length)));
            }
            if (await plaintext.ReadAsync(buffer.AsMemory(0, 1), ct).ConfigureAwait(false) != 0)
            {
                throw new CryptographicException("Source is longer than the declared plaintext length.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
        return chunks;
    }

    /// <summary>
    /// Decrypts all chunks in order. The chunk list must match the header's count exactly, so truncation and
    /// extension are detected before any plaintext is written.
    /// </summary>
    public static async Task DecryptAsync(IReadOnlyList<byte[]> chunks, ChunkCipher cipher, Stream output, CancellationToken ct = default)
    {
        if ((ulong)chunks.Count != cipher.Header.ChunkCount)
        {
            throw new CryptographicException("Chunk count does not match the file header.");
        }
        for (var i = 0; i < chunks.Count; i++)
        {
            var plain = cipher.DecryptChunk((uint)i, chunks[i]);
            await output.WriteAsync(plain, ct).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(plain);
        }
    }
}
