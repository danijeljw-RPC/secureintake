# S1: `SI-E2EE-1` End-to-End Encryption Protocol

| | |
| --- | --- |
| Status | **Draft, provisional. Not frozen.** The candidate values are accepted pending cryptographic review (2026-11-03). Freezing requires reviewer approval of §12 |
| Suite | `SI-E2EE-1`, suite code `0x0001` |
| Reference implementations | .NET `src/SecureIntake.Cryptography/E2ee` · TypeScript (WebCrypto) `frontend/packages/crypto-protocol/src/si-e2ee-1.ts` |
| Vectors | [`vectors/si-e2ee-1.json`](vectors/si-e2ee-1.json), verified by both implementations |
| Related | [doc 26](../architecture/26-cryptographic-specifications.md) · [ADR-0002](../adr/ADR-0002-libsodium-for-x25519.md) · S2 (`.sif` container) |

The key words MUST, MUST NOT, SHOULD and MAY are used as in RFC 2119.

## 1. Versioning rule

Every byte layout, label, length, KDF parameter and combiner in this document is part of the suite. A change that alters ciphertext, envelopes, digests or fingerprints, or stops either side from decrypting the other's output, MUST be released as a **new suite identifier** (for example `SI-E2EE-2`, suite code `0x0002`). It MUST NOT be released as an edit to `SI-E2EE-1`. Implementations dispatch on the suite code and MUST reject unknown codes. Editorial clarifications that change no bytes don't need a new suite.

## 2. Notation and conventions

- `‖` is byte concatenation. All lengths are in bytes.
- `u8(n)`, `u16be(n)`, `u32be(n)` and `u64be(n)` are unsigned big-endian integers of 1, 2, 4 and 8 bytes.
- `"text"` is the ASCII bytes of the text, with no terminator and no length prefix. Every label in this document is fixed and appears verbatim.
- `0x00` is a single zero byte.
- `HKDF(ikm, salt, info, L)` is HKDF-SHA-256 (RFC 5869), Extract then Expand, with output length `L`. An empty salt is a zero-length byte string, which RFC 5869 §2.2 treats as 32 zero bytes.
- `AES-GCM-Enc(k, n, p, a)` is AES-256-GCM with a 32-byte key, a 12-byte nonce, a 16-byte tag and AAD `a`. The output is `ciphertext ‖ tag`, with ciphertext length equal to plaintext length.
- `SHA-256(x)` is FIPS 180-4.
- Every multi-byte field in every structure has a fixed length. No structure in this suite has a variable-length field, so no length prefixes or delimiters are needed. That is what makes every encoding below unambiguous.

## 3. Primitives

| Function | Primitive | .NET | Browser |
| --- | --- | --- | --- |
| Classical KEM part | X25519 (RFC 7748) | libsodium `crypto_scalarmult_curve25519` (ADR-0002) | WebCrypto `X25519` |
| PQ KEM part | ML-KEM-768 (FIPS 203) | `System.Security.Cryptography.MLKem` (OpenSSL 3.5+ on Linux) | `@noble/post-quantum`, pinned and vendored (M2) |
| KDF | HKDF-SHA-256 | `HKDF` | WebCrypto `HKDF` |
| AEAD | AES-256-GCM | `AesGcm` | WebCrypto `AES-GCM` |
| Hash, MAC | SHA-256, HMAC-SHA-256 | BCL | WebCrypto |
| Randomness | Platform CSPRNG | `RandomNumberGenerator` | `crypto.getRandomValues` |

## 4. Fixed-length identifiers

| Field | Length | Encoding |
| --- | --- | --- |
| `suiteCode` | 2 | `u16be(0x0001)`. The text `"SI-E2EE-1"` appears only in JSON manifests and APIs, never in binary structures |
| `fileId` | 16 | The file's UUIDv7 in RFC 9562 byte order (big-endian). Fresh per file version. Never the `file_…` text form |
| `recipientKeyId` | 16 | The recipient key record's UUIDv7 in RFC 9562 byte order. Never the `key_…` text form |
| `DEK` | 32 | Uniformly random from the CSPRNG. Fresh per file version. MUST NOT be reused across files or versions |

## 5. Hybrid KEM

### 5.1 Keys

A recipient public key is `pk = (pkX, ek)`: a 32-byte X25519 public key (RFC 7748 u-coordinate, little-endian as RFC 7748 defines) and a 1184-byte ML-KEM-768 encapsulation key. Implementations MUST reject any other lengths.

A recipient private key is a 32-byte X25519 scalar `skX` and a 64-byte ML-KEM seed `d ‖ z` (FIPS 203 `ML-KEM.KeyGen_internal`). Private keys live only in reviewer clients and are never sent to the server.

### 5.2 Encapsulation (uploader)

1. Generate an ephemeral X25519 scalar `e` (32 bytes from the CSPRNG). Compute `ctX = X25519(e, 9)`, the ephemeral public key.
2. Compute `ssX = X25519(e, pkX)`. This is the raw 32-byte RFC 7748 output, not hashed. If `ssX` is all zero, abort: `pkX` is low-order or otherwise invalid.
3. Compute `(ssM, ctM) = ML-KEM-768.Encaps(ek)`. `ssM` is 32 bytes and `ctM` is 1088 bytes. The FIPS 203 §7.2 encapsulation-key input check MUST be performed (the platform library is relied on to do this; see review item R-4).
4. `ct = ctX ‖ ctM` (1120 bytes).
5. `ss = Combine(ssM, ssX, ctX, pkX)` (§5.4).
6. Erase `e`, `ssX` and `ssM`.

### 5.3 Decapsulation (reviewer)

1. Reject unless `len(ct) = 1120`. Split it into `ctX = ct[0..32)` and `ctM = ct[32..1120)`.
2. `ssX = X25519(skX, ctX)`. Abort if it's all zero.
3. `ssM = ML-KEM-768.Decaps(dk, ctM)`. With FIPS 203 implicit rejection, a modified `ctM` yields a pseudorandom `ssM` rather than an error. The DEK unwrap (§8) then fails authentication.
4. `ss = Combine(ssM, ssX, ctX, pkX)`, where `pkX` is the recipient's own X25519 public key.

### 5.4 Combiner (provisional)

```text
ikm = ssM(32) ‖ ssX(32) ‖ ctX(32) ‖ pkX(32)              128 bytes, fixed order
ss  = HKDF(ikm, salt = "" (empty), info = "SI-E2EE-1 kem", L = 32)
```

- **Inputs.** Four 32-byte values in the order shown. Implementations MUST reject inputs of any other length.
- **Domain separation.** The `info` label `"SI-E2EE-1 kem"` is unique to this step in this suite. Each other HKDF use in the suite has its own label (§7.1, §8).
- **Output length.** 32 bytes, used only as HKDF input key material in §8.
- **Rationale.**
  - `ss` stays secret as long as *either* component KEM is secure: HKDF-Extract is a randomness extractor over the whole IKM, so it remains pseudorandom if either `ssM` or `ssX` is.
  - Binding `ctX` and `pkX` gives the X25519 component the ciphertext and public-key binding a DH-based KEM lacks (the same reasoning as the X-Wing and DHKEM constructions).
  - The ML-KEM ciphertext and encapsulation key are *not* included. That follows X-Wing's argument that ML-KEM's FO transform already binds `ssM` to `ctM` and `ek`. Whether to include them anyway is review item R-1.
  - The empty salt is acceptable because `ikm` contains at least one full-entropy secret. A fixed non-empty salt is review item R-3.
- **Status.** Provisional, per your decision of 2026-11-03: keep this HKDF combiner, don't switch to X-Wing now. Before freezing, review it against hybrid-KEM guidance: X-Wing (`draft-connolly-cfrg-xwing-kem`), `draft-ietf-tls-hybrid-design`, NIST SP 800-227 on KEM combiners, and the GHP18 combiner results. Any later change is a new suite (§1).

## 6. File header and `manifestDigest`

The header contains only values fixed before any encryption starts. So `manifestDigest` can't depend on ciphertext, envelopes, chunk metadata or anything produced afterwards. In particular, adding a recipient by rewrap doesn't change it.

```text
header = "SIE1"(4) ‖ u16be(suiteCode) ‖ fileId(16) ‖ u32be(chunkSize = 4194304) ‖ u64be(plaintextLength) ‖ u32be(chunkCount)
                                                                                               38 bytes in total
chunkCount     = 1 if plaintextLength = 0, else ceil(plaintextLength / 4194304)
manifestDigest = SHA-256("SI-E2EE-1 manifest" ‖ 0x00 ‖ header)
```

- `chunkCount` MUST be ≤ 2³² − 1 (§7.3). A decoder MUST check the magic, the suite code, the chunk size and the length, and MUST recompute `chunkCount` from `plaintextLength` and reject any mismatch.
- `plaintextLength` is visible to the server. File sizes aren't hidden in v1 (doc 26 §2).
- The header MUST NOT contain a plaintext hash, even a keyed one. An unkeyed hash would give the server a confirmation oracle over the file contents.
- S2 (`.sif`) carries this header verbatim. S5 (Submission Evidence Manifest) references `manifestDigest`.

## 7. Content encryption

### 7.1 Key schedule

```text
contentKey(32) = HKDF(DEK, salt = fileId, info = "SI-E2EE-1 content", L = 32)
nonceBase(12)  = HKDF(DEK, salt = fileId, info = "SI-E2EE-1 nonce",   L = 12)
commitKey(32)  = HKDF(DEK, salt = fileId, info = "SI-E2EE-1 commit",  L = 32)
```

The DEK is never used directly as an AES key. Three distinct labels keep the three outputs independent.

### 7.2 Chunks, nonces and AAD

The plaintext is split into `chunkCount` chunks. Chunk `i` (`0 ≤ i < chunkCount`) has exactly 4,194,304 bytes, except the last, which has `plaintextLength − (chunkCount − 1) × 4194304` bytes (0 for an empty file).

```text
final_i = 1 if i = chunkCount − 1, else 0
nonce_i = nonceBase XOR ( u32be(i) ‖ u8(final_i) ‖ 0x00 × 7 )                    12 bytes
aad_i   = fileId(16) ‖ u32be(i) ‖ u8(final_i) ‖ manifestDigest(32)              53 bytes
ct_i    = AES-GCM-Enc(contentKey, nonce_i, chunk_i, aad_i)                      len(chunk_i) + 16
```

Decryption MUST reject:

- a chunk whose ciphertext length isn't `expectedPlaintextLength(i) + 16`
- an index ≥ `chunkCount`
- a chunk list whose count isn't `chunkCount`

All three checks happen before decryption is attempted. Authentication failure on any chunk is fatal for the file.

### 7.3 Nonce uniqueness and limits

- **Within a file.** `final_i` is a function of `i`, so each index has exactly one nonce. The mask `u32be(i) ‖ u8(final_i)` is injective in `i` for `i < 2³²`, so distinct indexes give distinct nonces. `chunkCount` is therefore limited to 2³² − 1. That's far above the operational ceiling (a 20 GiB maximum file is 5,120 chunks, per doc 27). It also keeps AES-GCM well inside its per-key invocation limit.
- **Across files.** Each file version has a fresh random DEK, and so independent `contentKey` and `nonceBase`. `(contentKey, nonce)` pairs never repeat across files except with negligible probability.
- **Truncation, extension and reordering.** These are detected by `i` and `final_i` in both the nonce and the AAD, together with `chunkCount` in `manifestDigest`.

### 7.4 Resume safety

Encryption is deterministic per `(file, i)`, so a resumed upload recomputes identical ciphertext for an unchanged chunk. If the source file changes between attempts, re-encrypting chunk `i` would reuse `nonce_i` with different plaintext, which is catastrophic for AES-GCM. Clients MUST therefore do the following:

1. Compute `commit_i = HMAC-SHA-256(commitKey, u32be(i) ‖ chunk_i)` and persist it locally **before** uploading `ct_i`.
2. On resume, before re-encrypting any chunk `i` that has a stored `commit_i`, recompute it from the current source. On any mismatch, abandon the file and restart it with a new `fileId` and DEK.
3. Never send `commit_i` to the server. It's keyed, so it isn't an oracle, but it has no server-side purpose.

### 7.5 Release of plaintext

A streaming decryptor MAY release each authenticated chunk. It MUST NOT treat the output as complete until the final chunk has authenticated, and it MUST surface a failure on any later chunk.

## 8. DEK wrap (one per recipient per file)

```text
kek     = HKDF(ss, salt = fileId, info = "SI-E2EE-1 wrap", L = 32)
aad     = u16be(suiteCode) ‖ fileId(16) ‖ recipientKeyId(16) ‖ recipientFingerprint(32)      66 bytes
nonce   = 12 bytes from the CSPRNG
wrapped = nonce ‖ AES-GCM-Enc(kek, nonce, DEK, aad)                                         60 bytes
envelope fields: recipientKeyId(16), ct(1120), wrapped(60)       (container serialisation: S2)
```

- `recipientFingerprint` is §9 computed over the recipient public key used for encapsulation. Binding it means an envelope opens only under the intended key record and the full intended public key, including the ML-KEM half.
- **Nonce reuse.** Each `kek` comes from a fresh encapsulation (fresh `e` and fresh ML-KEM randomness), and each `kek` encrypts exactly one message once. A `(kek, nonce)` pair therefore can't repeat. The nonce is still drawn from the CSPRNG as defence in depth. Implementations MUST NOT expose a wrap API that accepts an existing shared secret. In .NET, `KeyWrap` and `HybridKem` are `internal` and reachable only through `Envelope.Seal`.
- An unwrap failure MUST be reported as a generic decryption failure, without saying which check failed.

## 9. Recipient key fingerprint

```text
fingerprint = SHA-256("SI-E2EE-1 recipient key" ‖ 0x00 ‖ u16be(suiteCode) ‖ pkX(32) ‖ ek(1184))     32 bytes
```

The fingerprint is used in the wrap AAD (§8), recipient manifests (S4), the Organisation Key Directory (S4) and the OKD link pin (doc 26 §5). The suite code makes fingerprints from different suites distinct even for identical key bytes. When shown to people it's rendered as lowercase hex. Shorter renderings for display are defined in S4 and MUST NOT be used for comparison.

## 10. Handling of secrets

- Private keys, `e`, `ssX`, `ssM`, `ss`, `kek`, `contentKey`, `nonceBase`, `commitKey` and DEKs MUST NOT be logged, persisted by the server, or exposed through public APIs. Implementations SHOULD zero them after use where the platform allows; .NET does so with `CryptographicOperations.ZeroMemory`.
- In .NET, the X25519, KEM and key-wrap primitives are `internal` to `SecureIntake.Cryptography`. The public surface is `HybridPublicKey`, `HybridPrivateKey` (with no export), `Envelope`, `FileHeader`, `ChunkCipher` and `FileCodec`.
- The host runs a startup self-test: libsodium loads, the RFC 7748 known answer is correct, and ML-KEM is available. Production and Hardened refuse to start if the suite isn't usable.

## 11. Test vectors and negative tests

[`vectors/si-e2ee-1.json`](vectors/si-e2ee-1.json) contains:

| Section | Covers |
| --- | --- |
| `x25519` | RFC 7748 §6.1 |
| `x25519RejectedPublicKeys` | Seven keys that give an all-zero secret, which MUST be rejected |
| `combiner` | Inputs, IKM and output of §5.4 |
| `fingerprint` | §9 |
| `keyWrap` | `kek`, `aad` and `wrapped` with a fixed nonce |
| `files` | Lengths 0, 33, 4 MiB and 4 MiB + 5: header, `manifestDigest`, key schedule, and for every chunk the nonce, AAD, ciphertext (or its SHA-256 for large chunks) and resume commitment |
| `hybridEnvelope` | Seeds, derived public keys, a KEM ciphertext, `ssM`, `ss` and the wrapped DEK. The ML-KEM part of the ciphertext came from platform randomness, so it's verified by decapsulation |

Required negative tests are implemented in both languages: .NET in `tests/SecureIntake.UnitTests/E2eeTests.cs` and TypeScript in `frontend/packages/crypto-protocol/test`.

- **Altered AAD.** Another `fileId`, another declared length (and so another `manifestDigest`), a final chunk at a non-final index, and a non-final chunk under a header where it would be final. For the wrap: changed `fileId`, `recipientKeyId` or `recipientFingerprint`.
- **Wrong keys.** Wrong DEK, wrong KEK, wrong recipient private key.
- **Modified ciphertext.** Any byte of a chunk ciphertext or tag, of the wrapped DEK, or of either half of the KEM ciphertext.
- **Ordering.** Swapped chunks.
- **Final-chunk handling.** Truncated or extended chunk lists (rejected before any output), an index beyond `chunkCount`, wrong plaintext or ciphertext lengths, and the chunk-count limit.
- **Keys and headers.** Low-order X25519 keys, wrong key lengths, and malformed headers (magic, suite, chunk size, inconsistent count, length).

ML-KEM decapsulation of the hybrid vector is checked in .NET (on Alpine with OpenSSL 3.5) but not yet in TypeScript, which lacks an ML-KEM implementation until M2. TypeScript checks the X25519 half, the combiner and the unwrap from the recorded `ssM`.

## 12. Open items before freezing (for the reviewer)

| # | Item |
| --- | --- |
| R-1 | Combiner inputs: should `ctM` and/or `ek` (or a hash of them) be added to the IKM, as generic combiners require? X-Wing argues they aren't needed for ML-KEM |
| R-2 | Confirm staying with the HKDF combiner rather than X-Wing for v1. A later switch is a new suite |
| R-3 | Empty HKDF salt in §5.4 versus a fixed suite salt |
| R-4 | ML-KEM encapsulation-key validation (FIPS 203 §7.2) and decapsulation-key checks in both the .NET platform library and `@noble/post-quantum` |
| R-5 | Resume rule §7.4: is the local commitment sufficient, given that the server may hold an unconfirmed ciphertext for a chunk? |
| R-6 | Whether `plaintextLength` disclosure (§6) is acceptable for v1, or whether files should be padded like form payloads (S3) |
| R-7 | Browser X25519 availability (WebCrypto `X25519` support baseline) and the fallback if a target browser lacks it |

## 13. Changes from doc 26 §2 (candidate values, accepted provisionally on 2026-11-03)

- **Content key separated from the DEK.** Doc 26 used the DEK directly as the AES key and as HKDF input for the nonce. S1 derives `contentKey`, `nonceBase` and `commitKey` with distinct labels.
- **`manifestDigest` defined** (§6). It contains only pre-encryption values, which removes the circular dependency.
- **Fixed-length binary IDs.** `recipientKeyId` and `suiteId` are fixed-length binary (`u16be` suite code, 16-byte UUID) instead of length-prefixed text.
- **Recipient fingerprint in the wrap AAD.** The wrap AAD now carries the recipient fingerprint.
- **New fingerprint definition** (§9), with a label, the suite code and fixed lengths.
- **Resume commitment added** (§7.4).
