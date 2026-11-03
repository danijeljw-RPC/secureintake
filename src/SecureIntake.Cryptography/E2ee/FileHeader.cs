using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SecureIntake.Cryptography.E2ee;

/// <summary>
/// The content-binding header for one encrypted file (S1 §6). It holds only values fixed before any chunk
/// is encrypted, so <see cref="ManifestDigest"/> has no dependency on ciphertext, envelopes or anything
/// produced later (envelopes can be added by rewrap without changing it).
/// <code>
/// header = "SIE1"(4) ‖ u16be suiteCode ‖ fileId(16) ‖ u32be chunkSize ‖ u64be plaintextLength ‖ u32be chunkCount   (38 bytes)
/// manifestDigest = SHA-256("SI-E2EE-1 manifest" ‖ 0x00 ‖ header)
/// </code>
/// </summary>
public sealed class FileHeader
{
    public const int EncodedSize = 4 + 2 + 16 + 4 + 8 + 4;

    private readonly byte[] _fileId;

    public FileHeader(ReadOnlySpan<byte> fileId, ulong plaintextLength)
    {
        if (fileId.Length != SiE2ee1.FileIdSize)
        {
            throw new CryptographicException("File ID must be 16 bytes.");
        }
        var count = plaintextLength == 0 ? 1UL : ((plaintextLength - 1) / SiE2ee1.ChunkSize) + 1;
        if (count > SiE2ee1.MaxChunkCount)
        {
            throw new CryptographicException("File exceeds the SI-E2EE-1 chunk-count limit.");
        }
        _fileId = fileId.ToArray();
        PlaintextLength = plaintextLength;
        ChunkCount = (uint)count;
        Encoded = Encode();
        ManifestDigest = Digest(Encoded);
    }

    public ReadOnlySpan<byte> FileId => _fileId;
    public ulong PlaintextLength { get; }
    public uint ChunkCount { get; }
    public byte[] Encoded { get; }
    public byte[] ManifestDigest { get; }

    /// <summary>Plaintext length of chunk <paramref name="index"/>; every chunk but the last is exactly <see cref="SiE2ee1.ChunkSize"/>.</summary>
    public int ChunkPlaintextLength(uint index)
    {
        if (index >= ChunkCount)
        {
            throw new CryptographicException("Chunk index beyond the declared chunk count.");
        }
        return index < ChunkCount - 1 ? SiE2ee1.ChunkSize : (int)(PlaintextLength - ((ulong)(ChunkCount - 1) * SiE2ee1.ChunkSize));
    }

    public bool IsFinal(uint index) => index == ChunkCount - 1;

    /// <summary>Parses and validates an encoded header (exact length, magic, suite, chunk size, consistent count).</summary>
    public static FileHeader Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length != EncodedSize || !encoded[..4].SequenceEqual(SiE2ee1.HeaderMagic)
            || BinaryPrimitives.ReadUInt16BigEndian(encoded[4..]) != SiE2ee1.SuiteCode
            || BinaryPrimitives.ReadUInt32BigEndian(encoded[22..]) != SiE2ee1.ChunkSize)
        {
            throw new CryptographicException("Invalid SI-E2EE-1 file header.");
        }
        var header = new FileHeader(encoded.Slice(6, 16), BinaryPrimitives.ReadUInt64BigEndian(encoded[26..]));
        if (header.ChunkCount != BinaryPrimitives.ReadUInt32BigEndian(encoded[34..]))
        {
            throw new CryptographicException("File header chunk count is inconsistent with its length.");
        }
        return header;
    }

    private byte[] Encode()
    {
        var b = new byte[EncodedSize];
        SiE2ee1.HeaderMagic.CopyTo(b, 0);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), SiE2ee1.SuiteCode);
        _fileId.CopyTo(b, 6);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(22), SiE2ee1.ChunkSize);
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(26), PlaintextLength);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(34), ChunkCount);
        return b;
    }

    private static byte[] Digest(byte[] header)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(SiE2ee1.ManifestLabel);
        h.AppendData([0]);
        h.AppendData(header);
        return h.GetHashAndReset();
    }
}
