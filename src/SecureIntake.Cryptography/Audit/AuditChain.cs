using System.Security.Cryptography;
using SecureIntake.Cryptography.Canonical;

namespace SecureIntake.Cryptography.Audit;

/// <summary>
/// Audit hash chain (F11, spec S8 draft): <c>hash_n = SHA-256(hash_{n-1} ‖ JCS(record_n))</c>,
/// with <c>hash_0</c> (the predecessor of seq 1) being 32 zero bytes.
/// </summary>
public static class AuditChain
{
    public const int HashSize = 32;

    public static byte[] Genesis => new byte[HashSize];

    public static byte[] Link(ReadOnlySpan<byte> previousHash, ReadOnlySpan<byte> recordJsonUtf8)
    {
        if (previousHash.Length != HashSize)
        {
            throw new ArgumentException("Previous hash must be 32 bytes.", nameof(previousHash));
        }
        var canonical = Jcs.Canonicalize(recordJsonUtf8);
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(previousHash);
        h.AppendData(canonical);
        return h.GetHashAndReset();
    }

    /// <summary>
    /// Verifies a contiguous slice of the chain. Returns the index of the first bad record, or -1 when valid.
    /// </summary>
    public static int Verify(ReadOnlySpan<byte> startPreviousHash, IReadOnlyList<(byte[] RecordJson, byte[] StoredPrevHash, byte[] StoredHash)> records)
    {
        var prev = startPreviousHash.ToArray();
        for (var i = 0; i < records.Count; i++)
        {
            var (json, storedPrev, storedHash) = records[i];
            if (!CryptographicOperations.FixedTimeEquals(prev, storedPrev))
            {
                return i;
            }
            var computed = Link(prev, json);
            if (!CryptographicOperations.FixedTimeEquals(computed, storedHash))
            {
                return i;
            }
            prev = computed;
        }
        return -1;
    }
}
