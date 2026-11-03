using System.Security.Cryptography;
using System.Text.Json;
using SecureIntake.Cryptography;
using SecureIntake.Cryptography.E2ee;

namespace SecureIntake.UnitTests;

/// <summary>Skips when the platform lacks ML-KEM (macOS; Linux before OpenSSL 3.5). CI runs these on Alpine.</summary>
public sealed class MlKemFactAttribute : FactAttribute
{
    public MlKemFactAttribute()
    {
        if (!HybridKem.IsSupported)
        {
            Skip = "ML-KEM-768 not supported on this platform";
        }
    }
}

/// <summary>Recomputes every deterministic value in docs/specs/vectors/si-e2ee-1.json from that file's inputs.</summary>
public class SiE2ee1VectorTests
{
    private static readonly JsonElement V = JsonDocument.Parse(File.ReadAllBytes(VectorGenerator.VectorPath)).RootElement;

    private static byte[] H(JsonElement e, string name) => Convert.FromHexString(e.GetProperty(name).GetString()!);

    [Fact]
    public void X25519_rfc7748()
    {
        foreach (var v in V.GetProperty("x25519").EnumerateArray())
        {
            Assert.Equal(H(v, "alicePk"), X25519.PublicKeyFrom(H(v, "aliceSk")));
            Assert.Equal(H(v, "bobPk"), X25519.PublicKeyFrom(H(v, "bobSk")));
            Assert.Equal(H(v, "shared"), X25519.SharedSecret(H(v, "aliceSk"), H(v, "bobPk")));
            Assert.Equal(H(v, "shared"), X25519.SharedSecret(H(v, "bobSk"), H(v, "alicePk")));
        }
    }

    [Fact]
    public void X25519_rejects_keys_that_give_an_all_zero_secret()
    {
        var (sk, _) = X25519.GenerateKeyPair();
        foreach (var pk in V.GetProperty("x25519RejectedPublicKeys").EnumerateArray())
        {
            Assert.Throws<CryptographicException>(() => X25519.SharedSecret(sk, Convert.FromHexString(pk.GetString()!)));
        }
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public void X25519_rejects_wrong_lengths(int length)
    {
        var (sk, _) = X25519.GenerateKeyPair();
        Assert.Throws<CryptographicException>(() => X25519.SharedSecret(sk, new byte[length]));
        Assert.Throws<CryptographicException>(() => X25519.PublicKeyFrom(new byte[length]));
    }

    [Fact]
    public void Combiner()
    {
        foreach (var v in V.GetProperty("combiner").EnumerateArray())
        {
            byte[] ikm = [.. H(v, "ssMlKem"), .. H(v, "ssX25519"), .. H(v, "ctX25519"), .. H(v, "pkX25519")];
            Assert.Equal(H(v, "ikm"), ikm);
            Assert.Equal(H(v, "sharedSecret"), HybridKem.Combine(H(v, "ssMlKem"), H(v, "ssX25519"), H(v, "ctX25519"), H(v, "pkX25519")));
        }
    }

    [Fact]
    public void Fingerprint()
    {
        foreach (var v in V.GetProperty("fingerprint").EnumerateArray())
        {
            Assert.Equal(H(v, "fingerprint"), new HybridPublicKey(H(v, "x25519Pk"), H(v, "mlKemEk")).Fingerprint());
        }
    }

    [Fact]
    public void Key_wrap()
    {
        foreach (var v in V.GetProperty("keyWrap").EnumerateArray())
        {
            var (ss, dek, fileId, keyId, fp) = (H(v, "kemSharedSecret"), H(v, "dek"), H(v, "fileId"), H(v, "recipientKeyId"), H(v, "recipientFingerprint"));
            Assert.Equal(H(v, "kek"), KeyWrap.DeriveKek(ss, fileId));
            Assert.Equal(H(v, "aad"), KeyWrap.Aad(fileId, keyId, fp));
            Assert.Equal(H(v, "wrapped"), KeyWrap.Wrap(ss, dek, fileId, keyId, fp, H(v, "nonce")));
            Assert.Equal(dek, KeyWrap.Unwrap(ss, H(v, "wrapped"), fileId, keyId, fp));
        }
    }

    [Fact]
    public void Files_headers_key_schedule_nonces_aad_and_chunks()
    {
        foreach (var v in V.GetProperty("files").EnumerateArray())
        {
            var header = new FileHeader(H(v, "fileId"), v.GetProperty("plaintextLength").GetUInt64());
            Assert.Equal(H(v, "header"), header.Encoded);
            Assert.Equal(H(v, "manifestDigest"), header.ManifestDigest);
            Assert.Equal(H(v, "header"), FileHeader.Decode(H(v, "header")).Encoded);

            using var cipher = new ChunkCipher(H(v, "dek"), header);
            var plain = VectorGenerator.Pattern((int)header.PlaintextLength);
            var chunks = v.GetProperty("chunks").EnumerateArray().ToList();
            Assert.Equal((int)header.ChunkCount, chunks.Count);
            foreach (var c in chunks)
            {
                var i = c.GetProperty("index").GetUInt32();
                var (nonce, aad) = cipher.NonceAndAad(i);
                Assert.Equal(H(c, "nonce"), nonce);
                Assert.Equal(H(c, "aad"), aad);
                Assert.Equal(c.GetProperty("final").GetBoolean(), header.IsFinal(i));
                var chunkPlain = plain.AsSpan((int)(i * (ulong)SiE2ee1.ChunkSize), header.ChunkPlaintextLength(i));
                var ct = cipher.EncryptChunk(i, chunkPlain);
                Assert.Equal(H(c, "ciphertextSha256"), SHA256.HashData(ct));
                if (c.TryGetProperty("ciphertext", out _))
                {
                    Assert.Equal(H(c, "ciphertext"), ct);
                }
                Assert.Equal(H(c, "resumeCommitment"), cipher.ResumeCommitment(i, chunkPlain));
                Assert.Equal(chunkPlain.ToArray(), cipher.DecryptChunk(i, ct));
            }
        }
    }

    [MlKemFact]
    public void Hybrid_envelope_opens_by_decapsulation()
    {
        Assert.True(V.TryGetProperty("hybridEnvelope", out var v), "Vector file has no hybrid section; regenerate with SECUREINTAKE_WRITE_VECTORS=all on Linux.");
        using var key = HybridPrivateKey.FromSeeds(H(v, "x25519Sk"), H(v, "mlKemSeed"));
        Assert.Equal(H(v, "x25519Pk"), key.PublicKey.X25519.ToArray());
        Assert.Equal(H(v, "mlKemEk"), key.PublicKey.MlKem768.ToArray());
        Assert.Equal(H(v, "fingerprint"), key.PublicKey.Fingerprint());
        Assert.Equal(H(v, "ephemeralX25519Sk") is var eph ? X25519.PublicKeyFrom(eph) : null, H(v, "kemCiphertext")[..32]);
        Assert.Equal(H(v, "ssMlKem"), key.MlKemDecapsulate(H(v, "kemCiphertext").AsSpan(32)));
        Assert.Equal(H(v, "sharedSecret"), HybridKem.Decapsulate(key, H(v, "kemCiphertext")));
        var env = new Envelope(new Guid(H(v, "recipientKeyId"), bigEndian: true), H(v, "kemCiphertext"), H(v, "wrappedDek"));
        Assert.Equal(H(v, "dek"), env.Open(key, H(v, "fileId")));
    }

    [Fact]
    public void Self_test_reports_libsodium()
    {
        var r = CryptoSelfTest.Run();
        Assert.True(r.LibsodiumAvailable);
        Assert.True(r.X25519KnownAnswer);
        Assert.Equal(HybridKem.IsSupported, r.MlKemAvailable);
    }
}

/// <summary>Negative tests for content encryption (S1 §11).</summary>
public class ChunkCipherNegativeTests
{
    private static readonly byte[] Dek = VectorGenerator.Dek;
    private static readonly byte[] FileId = VectorGenerator.FileId;
    private static readonly ulong TwoChunksPlus = (2UL * SiE2ee1.ChunkSize) + 7;

    private static (FileHeader Header, ChunkCipher Cipher, List<byte[]> Chunks) Encrypt(ulong length, byte[]? dek = null)
    {
        var header = new FileHeader(FileId, length);
        var cipher = new ChunkCipher(dek ?? Dek, header);
        var chunks = FileCodec.EncryptAsync(new MemoryStream(VectorGenerator.Pattern((int)length)), cipher).GetAwaiter().GetResult();
        return (header, cipher, chunks);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData((ulong)SiE2ee1.ChunkSize)]
    [InlineData((ulong)SiE2ee1.ChunkSize + 1)]
    public async Task Round_trip(ulong length)
    {
        var (_, cipher, chunks) = Encrypt(length);
        using (cipher)
        {
            var output = new MemoryStream();
            await FileCodec.DecryptAsync(chunks, cipher, output);
            Assert.Equal(VectorGenerator.Pattern((int)length), output.ToArray());
        }
    }

    [Fact]
    public void Wrong_dek_fails()
    {
        var (header, cipher, chunks) = Encrypt(33);
        cipher.Dispose();
        using var wrong = new ChunkCipher(RandomNumberGenerator.GetBytes(32), header);
        Assert.ThrowsAny<CryptographicException>(() => wrong.DecryptChunk(0, chunks[0]));
    }

    [Fact]
    public void Altered_aad_fails_other_file_id_or_manifest()
    {
        var (_, cipher, chunks) = Encrypt(33);
        cipher.Dispose();
        using var otherFile = new ChunkCipher(Dek, new FileHeader(RandomNumberGenerator.GetBytes(16), 33));
        Assert.ThrowsAny<CryptographicException>(() => otherFile.DecryptChunk(0, chunks[0]));
        // A header that claims a different length changes manifestDigest (and here also the expected chunk size).
        using var otherLength = new ChunkCipher(Dek, new FileHeader(FileId, 34));
        Assert.ThrowsAny<CryptographicException>(() => otherLength.DecryptChunk(0, chunks[0]));
    }

    [Fact]
    public void Modified_ciphertext_or_tag_fails()
    {
        var (_, cipher, chunks) = Encrypt(33);
        using (cipher)
        {
            foreach (var pos in new[] { 0, 16, chunks[0].Length - 1 })
            {
                var tampered = (byte[])chunks[0].Clone();
                tampered[pos] ^= 0x01;
                Assert.ThrowsAny<CryptographicException>(() => cipher.DecryptChunk(0, tampered));
            }
        }
    }

    [Fact]
    public async Task Reordered_chunks_fail()
    {
        var (_, cipher, chunks) = Encrypt(TwoChunksPlus);
        using (cipher)
        {
            (chunks[0], chunks[1]) = (chunks[1], chunks[0]);
            await Assert.ThrowsAnyAsync<CryptographicException>(() => FileCodec.DecryptAsync(chunks, cipher, new MemoryStream()));
        }
    }

    [Fact]
    public async Task Truncated_or_extended_files_fail_before_output()
    {
        var (_, cipher, chunks) = Encrypt(TwoChunksPlus);
        using (cipher)
        {
            var output = new MemoryStream();
            await Assert.ThrowsAnyAsync<CryptographicException>(() => FileCodec.DecryptAsync(chunks[..^1], cipher, output));
            await Assert.ThrowsAnyAsync<CryptographicException>(() => FileCodec.DecryptAsync([.. chunks, chunks[^1]], cipher, output));
            Assert.Equal(0, output.Length);
        }
    }

    [Fact]
    public void Final_chunk_handling()
    {
        var (header, cipher, chunks) = Encrypt(TwoChunksPlus);
        using (cipher)
        {
            Assert.True(header.IsFinal(2));
            Assert.False(header.IsFinal(1));
            // A non-final chunk presented as the final one, and the final chunk at a non-final position, both fail.
            using var shorter = new ChunkCipher(Dek, new FileHeader(FileId, 2UL * SiE2ee1.ChunkSize));
            Assert.ThrowsAny<CryptographicException>(() => shorter.DecryptChunk(1, chunks[1]));
            Assert.ThrowsAny<CryptographicException>(() => cipher.DecryptChunk(1, chunks[2]));
            // Index beyond the count, and wrong plaintext sizes, are rejected before any AES call.
            Assert.ThrowsAny<CryptographicException>(() => cipher.DecryptChunk(3, chunks[2]));
            Assert.ThrowsAny<CryptographicException>(() => cipher.EncryptChunk(0, new byte[100]));
            Assert.ThrowsAny<CryptographicException>(() => cipher.EncryptChunk(2, new byte[8]));
        }
    }

    [Fact]
    public void Header_decode_rejects_inconsistent_or_foreign_headers()
    {
        var good = new FileHeader(FileId, 33).Encoded;
        foreach (var (pos, value) in new[] { (0, (byte)'X'), (5, (byte)2), (25, (byte)1), (37, (byte)9) })
        {
            var bad = (byte[])good.Clone();
            bad[pos] = value;
            Assert.ThrowsAny<CryptographicException>(() => FileHeader.Decode(bad));
        }
        Assert.ThrowsAny<CryptographicException>(() => FileHeader.Decode(good.AsSpan(0, good.Length - 1)));
    }

    [Fact]
    public void Chunk_count_limit_is_enforced() =>
        Assert.ThrowsAny<CryptographicException>(() => new FileHeader(FileId, ((ulong)uint.MaxValue * SiE2ee1.ChunkSize) + 1));

    [Fact]
    public void Nonces_are_distinct_across_indexes_and_deterministic_for_resume()
    {
        var header = new FileHeader(FileId, 10UL * SiE2ee1.ChunkSize);
        using var a = new ChunkCipher(Dek, header);
        using var b = new ChunkCipher(Dek, header);
        var nonces = Enumerable.Range(0, 10).Select(i => Convert.ToHexString(a.NonceAndAad((uint)i).Nonce)).ToList();
        Assert.Equal(10, nonces.Distinct().Count());
        Assert.Equal(a.NonceAndAad(4).Nonce, b.NonceAndAad(4).Nonce);
    }

    [Fact]
    public void Resume_commitment_detects_changed_plaintext()
    {
        using var c = new ChunkCipher(Dek, new FileHeader(FileId, 33));
        var p = VectorGenerator.Pattern(33);
        var changed = (byte[])p.Clone();
        changed[5] ^= 1;
        Assert.NotEqual(c.ResumeCommitment(0, p), c.ResumeCommitment(0, changed));
    }
}

/// <summary>Negative tests for key wrap and envelopes (S1 §11).</summary>
public class KeyWrapNegativeTests
{
    private static readonly byte[] Ss = RandomNumberGenerator.GetBytes(32);
    private static readonly byte[] Fp = RandomNumberGenerator.GetBytes(32);
    private static readonly byte[] KeyId = VectorGenerator.KeyId;
    private static readonly byte[] FileId = VectorGenerator.FileId;

    [Fact]
    public void Altered_aad_fields_fail()
    {
        var wrapped = KeyWrap.Wrap(Ss, VectorGenerator.Dek, FileId, KeyId, Fp);
        Assert.ThrowsAny<CryptographicException>(() => KeyWrap.Unwrap(Ss, wrapped, RandomNumberGenerator.GetBytes(16), KeyId, Fp));
        Assert.ThrowsAny<CryptographicException>(() => KeyWrap.Unwrap(Ss, wrapped, FileId, RandomNumberGenerator.GetBytes(16), Fp));
        Assert.ThrowsAny<CryptographicException>(() => KeyWrap.Unwrap(Ss, wrapped, FileId, KeyId, RandomNumberGenerator.GetBytes(32)));
    }

    [Fact]
    public void Wrong_kek_or_modified_wrap_fails()
    {
        var wrapped = KeyWrap.Wrap(Ss, VectorGenerator.Dek, FileId, KeyId, Fp);
        Assert.ThrowsAny<CryptographicException>(() => KeyWrap.Unwrap(RandomNumberGenerator.GetBytes(32), wrapped, FileId, KeyId, Fp));
        foreach (var pos in new[] { 0, 12, 59 })
        {
            var t = (byte[])wrapped.Clone();
            t[pos] ^= 1;
            Assert.ThrowsAny<CryptographicException>(() => KeyWrap.Unwrap(Ss, t, FileId, KeyId, Fp));
        }
    }

    [Fact]
    public void Wrap_nonces_are_fresh() =>
        Assert.False(KeyWrap.Wrap(Ss, VectorGenerator.Dek, FileId, KeyId, Fp).AsSpan(0, 12).SequenceEqual(KeyWrap.Wrap(Ss, VectorGenerator.Dek, FileId, KeyId, Fp).AsSpan(0, 12)));

    [Fact]
    public void Combiner_binds_every_input()
    {
        byte[] m = RandomNumberGenerator.GetBytes(32), x = RandomNumberGenerator.GetBytes(32),
               ct = RandomNumberGenerator.GetBytes(32), pk = RandomNumberGenerator.GetBytes(32);
        var baseline = HybridKem.Combine(m, x, ct, pk);
        Assert.NotEqual(baseline, HybridKem.Combine(RandomNumberGenerator.GetBytes(32), x, ct, pk));
        Assert.NotEqual(baseline, HybridKem.Combine(m, RandomNumberGenerator.GetBytes(32), ct, pk));
        Assert.NotEqual(baseline, HybridKem.Combine(m, x, RandomNumberGenerator.GetBytes(32), pk));
        Assert.NotEqual(baseline, HybridKem.Combine(m, x, ct, RandomNumberGenerator.GetBytes(32)));
        Assert.Throws<CryptographicException>(() => HybridKem.Combine(m, x, ct, new byte[31]));
    }

    [Fact]
    public void Public_key_lengths_are_fixed()
    {
        Assert.Throws<CryptographicException>(() => new HybridPublicKey(new byte[32], new byte[1183]));
        Assert.Throws<CryptographicException>(() => new HybridPublicKey(new byte[31], new byte[1184]));
    }

    [MlKemFact]
    public void Envelope_opens_only_for_the_intended_recipient_key_id_and_file()
    {
        using var alice = HybridPrivateKey.Generate();
        using var mallory = HybridPrivateKey.Generate();
        var keyId = Guid.CreateVersion7();
        var env = Envelope.Seal(alice.PublicKey, keyId, VectorGenerator.Dek, FileId);
        Assert.Equal(HybridKem.CiphertextSize, env.KemCiphertext.Length);
        Assert.Equal(VectorGenerator.Dek, env.Open(alice, FileId));
        Assert.ThrowsAny<CryptographicException>(() => env.Open(mallory, FileId));
        Assert.ThrowsAny<CryptographicException>(() => env.Open(alice, RandomNumberGenerator.GetBytes(16)));
        Assert.ThrowsAny<CryptographicException>(() => (env with { RecipientKeyId = Guid.CreateVersion7() }).Open(alice, FileId));
    }

    [MlKemFact]
    public void Modified_kem_ciphertext_fails_in_either_half()
    {
        using var alice = HybridPrivateKey.Generate();
        var env = Envelope.Seal(alice.PublicKey, Guid.CreateVersion7(), VectorGenerator.Dek, FileId);
        foreach (var pos in new[] { 0, 31, 32, HybridKem.CiphertextSize - 1 })
        {
            var ct = (byte[])env.KemCiphertext.Clone();
            ct[pos] ^= 1;
            // X25519 half: different shared secret. ML-KEM half: implicit rejection yields a pseudorandom secret.
            Assert.ThrowsAny<CryptographicException>(() => (env with { KemCiphertext = ct }).Open(alice, FileId));
        }
    }

    [MlKemFact]
    public void Private_key_from_seeds_is_deterministic()
    {
        var x = RandomNumberGenerator.GetBytes(32);
        var seed = RandomNumberGenerator.GetBytes(64);
        using var a = HybridPrivateKey.FromSeeds(x, seed);
        using var b = HybridPrivateKey.FromSeeds(x, seed);
        Assert.Equal(a.PublicKey.Fingerprint(), b.PublicKey.Fingerprint());
    }
}
