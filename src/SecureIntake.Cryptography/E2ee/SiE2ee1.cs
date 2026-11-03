using System.Text;

namespace SecureIntake.Cryptography.E2ee;

/// <summary>
/// Constants for suite <c>SI-E2EE-1</c>. Normative definition: <c>docs/specs/S1-si-e2ee-1.md</c>.
/// <b>PROVISIONAL, pending external review.</b> Production and Hardened refuse to start while
/// <see cref="Reviewed"/> is false. Any change to an encoding, label or combiner that affects ciphertext
/// compatibility requires a new suite identifier, not an edit here.
/// </summary>
public static class SiE2ee1
{
    public const string SuiteId = "SI-E2EE-1";
    /// <summary>Two-byte suite code used in binary encodings (big-endian).</summary>
    public const ushort SuiteCode = 0x0001;
    public const bool Reviewed = false;

    public const int ChunkSize = 4 * 1024 * 1024;
    /// <summary>Chunk indexes are uint32, so a file has at most 2^32 − 1 chunks (index 0 … 2^32 − 2).</summary>
    public const uint MaxChunkCount = uint.MaxValue;
    public const int DekSize = 32;
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int FileIdSize = 16;
    public const int KeyIdSize = 16;
    public const int DigestSize = 32;
    public const int X25519PublicKeySize = 32;
    public const int MlKem768EncapsulationKeySize = 1184;
    public const int MlKem768CiphertextSize = 1088;
    public const int MlKem768SeedSize = 64;

    // Domain-separation labels. ASCII, used verbatim as HKDF info or hash prefixes.
    internal static readonly byte[] KemInfo = Ascii("SI-E2EE-1 kem");
    internal static readonly byte[] WrapInfo = Ascii("SI-E2EE-1 wrap");
    internal static readonly byte[] ContentInfo = Ascii("SI-E2EE-1 content");
    internal static readonly byte[] NonceInfo = Ascii("SI-E2EE-1 nonce");
    internal static readonly byte[] CommitInfo = Ascii("SI-E2EE-1 commit");
    internal static readonly byte[] ManifestLabel = Ascii("SI-E2EE-1 manifest");
    internal static readonly byte[] FingerprintLabel = Ascii("SI-E2EE-1 recipient key");
    internal static readonly byte[] HeaderMagic = Ascii("SIE1");

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
}
