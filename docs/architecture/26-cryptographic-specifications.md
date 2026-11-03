# Cryptographic Specifications and Review Plan

Status: **planning decision, 2026-11-03.** Answers [Q-GEN-7](../QUESTIONS.md), Q-KEY-1, Q-KEY-2, Q-KEY-3, Q-PRT-3, Q-FRM-2 and Q-EVD-2.

## 1. Rule

**No production release depends on unreviewed cryptography.** Internal alpha builds can run on draft specs, but only under the Development profile, which shows a "pre-review crypto" banner. Production and Hardened profiles refuse to start on a build whose `protocols.encryption` suite isn't marked `reviewed` in the release manifest.

## 2. Algorithm suites (Q-KEY-1)

### E2EE suite `SI-E2EE-1` (v1)

| Function | Primitive |
| --- | --- |
| Recipient KEM | **Hybrid X25519 + ML-KEM-768**, using the **X-Wing** combiner (IETF draft `draft-connolly-cfrg-xwing-kem`) if it's stable at spec freeze. Otherwise a spec-defined combiner, `HKDF-SHA-256(ss_mlkem ‖ ss_x25519 ‖ ct_x25519 ‖ pk_x25519, info = "SI-E2EE-1 kem")`, which must pass review |
| Key wrap | AES-256-GCM over the 32-byte DEK, keyed from the KEM shared secret via HKDF-SHA-256, with AAD binding `fileId ‖ recipientKeyId ‖ suiteId` |
| Content | AES-256-GCM per chunk, with a 4 MiB plaintext chunk size fixed by the protocol |
| Per-chunk nonce | `nonceBase(96) XOR (uint32 chunkIndex ‖ uint8 finalFlag ‖ zero padding)`, where `nonceBase = HKDF(DEK, fileId, "SI-E2EE-1 nonce")`. Every file gets a fresh random DEK, and a DEK is never reused across files or file versions. That makes nonces deterministic per (file, index) and safe to recompute on resume ([13](13-resumable-uploads.md) §15) |
| Chunk AAD | `fileId ‖ chunkIndex ‖ finalFlag ‖ manifestDigest`. This detects truncation, reordering and duplication |
| Hash | SHA-256 |

**Why hybrid PQ:** evidence submitted today may need to stay confidential for decades, which makes harvest-now-decrypt-later a real threat. X25519 keeps the security floor classical if ML-KEM implementations prove flawed.

**Fallback if review rejects browser ML-KEM:** ship `SI-E2EE-1` as X25519-only, and introduce the hybrid as `SI-E2EE-2` through suite agility. The format already carries `suiteId`, so no format change is needed.

### Signing suite `SI-SIG-1` (v1)

**ES256** (ECDSA P-256, SHA-256) for receipts, evidence packages, audit checkpoints and the Organisation Key Directory. Every KMS and HSM supports it, and so do WebCrypto and .NET.

Post-quantum signatures (ML-DSA) aren't in v1. Signatures are verified at the time of use, and their long-term integrity is better served by external checkpoint anchoring. PQ signing will come in as `SI-SIG-2` once KMS/HSM support is common.

### Key derivation for portable keys (`.sik`)

**Argon2id**: m = 256 MiB, t = 3, p = 1 by default. Clients refuse any m above 1 GiB or t above 10 (bounded parameters) and use AES-256-GCM.

### Form payload padding (Q-FRM-2)

Padding is **on by default**. An encrypted form payload is padded to the next bucket of 1 KiB, 4 KiB, 16 KiB, 64 KiB or 256 KiB, then to the next multiple of 256 KiB. Files aren't padded in v1, and ciphertext length is visible to the server. This is documented as a limitation.

## 3. Libraries (Q-PRT-3)

| Platform | Library |
| --- | --- |
| Browser | **WebCrypto** for AES-GCM, HKDF, SHA-256, X25519 and ECDSA P-256 verify. **`@noble/post-quantum`** for ML-KEM-768, version-pinned, vendored into the portal bundle and covered by the release integrity manifest. Swap to native WebCrypto ML-KEM when it ships in all target browsers. Argon2id (browser `.sik`, v1.1+) uses `hash-wasm`, pinned |
| .NET | `System.Security.Cryptography`: `AesGcm`, `HKDF`, `ECDsa`, and `MLKem` (.NET 10). X25519 isn't in the .NET BCL, so use a reviewed AOT-compatible implementation (NSec over libsodium, or a vetted managed implementation) chosen in the spec phase. Argon2id via libsodium (`Konscious` isn't AOT-verified). Linux needs OpenSSL 3.5+ for ML-KEM, which becomes a documented host requirement |
| Interop | Browser and .NET implementations must pass the **same** test vectors. Browser↔CLI round-trip tests run in CI on every change to `SecureIntake.Cryptography` or `frontend/packages/crypto-protocol` |

Browser ML-KEM is the single riskiest dependency. It goes to the reviewer as a named scope item.

## 4. Specifications to write

All specs live in `docs/specs/` and are versioned independently of the application ([20](20-deployment-packaging-and-upgrades.md) §8).

| # | Spec | Covers | Needed by |
| --- | --- | --- | --- |
| S1 | `SI-E2EE-1` protocol | Suite, KEM combiner, key wrap, chunking, nonces, AAD, errors | M2 |
| S2 | `.sif` file format | Header, canonical encoding (deterministic CBOR, RFC 8949 §4.2), bounds, manifest, envelopes, chunks | M2 |
| S3 | Encrypted form payload | Field encoding, padding, binding to the form snapshot | M2 |
| S4 | Recipient manifest and Organisation Key Directory | Manifest structure, OKD snapshot signing, link fingerprint pinning | M2 |
| S5 | Submission Evidence Manifest | Canonical (JCS) manifest that receipts commit to | M2 |
| S6 | `.sir.json` receipt | JWS JSON Serialization profile, signed and unsigned fields, verifier output states | M2 |
| S7 | `.sik` key package | Argon2id parameters and bounds, AEAD, metadata authentication | v1.0 |
| S8 | Audit chain and checkpoints | Record canonicalisation, chain, checkpoint signing, recovery epochs | M1 |
| S9 | `.siep` evidence package | ZIP64 profile, manifest, signing, encryption | v1.1 |
| S10 | Token formats | `siu_`, `sir_` and resume tokens, keyed digests | M2 |

Each spec includes test vectors, generated by the .NET reference implementation and independently checked by the TypeScript implementation. The vector list is in [08](08-reviewer-clients.md) §9.

## 5. Key substitution protection (Q-KEY-2)

**Threat:** a compromised server hands the uploader attacker-controlled recipient public keys.

**Decision: Organisation Key Directory (OKD).**

1. Each organisation has an OKD signing key (ES256). It's held by the Security Officer's `secureintake-client` (desktop or CLI) or an organisation KMS, **never by the server**.
2. When the recipient set changes, the Security Officer's client signs a **directory snapshot** (`orgId`, `version`, `[keyId, fingerprint, role, validity]`). The server stores and serves it but can't forge it.
3. Upload links and invitations carry the OKD key fingerprint in the URL fragment (`#siu_…&okd=<fp>`). The portal checks that every key in the recipient manifest appears in a snapshot signed by that pinned key.
4. If the check fails, the upload is refused before any encryption.

| Profile | OKD |
| --- | --- |
| Production | Optional per organisation. Without it, recipient keys rely on server trust plus the audit trail, and the admin UI states this |
| Hardened | **Required.** Dropbox activation fails without a current signed snapshot |

**Residual risk, documented plainly:** a compromised server that also serves malicious portal JavaScript can skip the check entirely. Mitigations are static portal hosting on separate infrastructure ([07](07-upload-portal-distribution.md)), a published release integrity manifest, and the independently distributed CLI for high-assurance uploaders later. Product docs must not claim more than this.

## 6. Threshold recovery (Q-KEY-3)

**Deferred.** v1 supports **multiple independent recovery recipients**: each custodian has their own recovery key, and any one of them can recover. The platform layer requires an approval quorum before ciphertext is released for recovery. True k-of-n secret sharing is revisited only if a customer needs it, and only with a reviewed library. Never an in-house scheme.

## 7. Key domains (Q-EVD-2)

Confirmed: **evidence package signing is a separate key domain** from receipt signing and audit checkpoint signing ([24](24-key-governance.md) §1). All three are held by the deployment (a protected local key, or KMS/HSM where configured) and are separate keys with separate IDs. Organisation-managed signing is post-v1.

## 8. Independent review

| Item | Plan |
| --- | --- |
| Who writes the specs | Drafted in Phase 0 (M0) alongside the reference implementation. One named owner signs off each spec |
| Reviewer | An external firm with applied-crypto **and** browser experience (candidates include NCC Group, Trail of Bits and Cure53). **Owner: you, for selection and contract** |
| Scope | S1 to S6, S8 and S10, the browser crypto module and its dependency on `@noble/post-quantum`, the .NET crypto module, test vectors, and the threat model. S7 and S9 get a smaller follow-up review |
| Timing | Engage at the start of M0. Review the frozen specs at the end of M0 and the implementation at the end of M2. Production v1.0 is gated on closure of all High and Critical findings |
| Output | A published report summary. Each suite gets marked `reviewed` in the release manifest |
| Scheduling note | Reviewer lead times are often 6 to 12 weeks. This sits on the critical path |
