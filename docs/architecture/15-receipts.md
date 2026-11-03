# Signed Submission Receipts

Source: notes v3.5. Key lifecycle and trust registries are generalised in [24](24-key-governance.md). Resolved questions: [QUESTIONS.md](../QUESTIONS.md) (Q-RCP-*). Planning decisions applied below take precedence over the source notes.

> **Planning decisions applied (2026-11-03)** — these override the text below where they differ. See [QUESTIONS.md](../QUESTIONS.md).
> - ES256 is confirmed. No PQ signatures in v1 (Q-KEY-1).
> - No TSA in v1 (optional in v1.2). Hardened requires the external audit checkpoint sink instead (Q-RCP-1).
> - No PDF receipt in v1. A printable HTML receipt view replaces it (Q-RCP-2). Wire format: spec S6 in [26](26-cryptographic-specifications.md).

## Decision

Every successfully finalised submission gets a **cryptographically signed receipt**, verifiable **independently**, without contacting the server or holding a decryption key. Versioned, portable format using asymmetric signatures with explicit commitments to ciphertext and the submission manifest.

**A receipt proves** a particular deployment acknowledged a particular encrypted submission at a stated time. **It does not prove** plaintext contents, the uploader's real-world identity, clock correctness, or that the organisation processed the submission.

> Report **receipt authenticity**, **signer trust** and **evidence integrity** separately — never collapse them into one "verified".

## 1. Architecture

```text
Uploader finalises (ciphertext objects · form response · recipient envelopes)
→ Server verifies stored ciphertext (existence · length · digest · manifest consistency)
→ Canonical submission commitment (immutable manifest · file hashes · encrypted form payload · metadata commitment)
→ Receipt signing service (deployment signing identity · signature · key ID)
→ Portable receipt (download · retain · share)
→ Independent verifier (signature · trusted public key · manifest digests)
```

Receipts belong to the finalisation pipeline, not notifications; notifications only deliver/reference them.

## 2. Format

| Format | Purpose |
| --- | --- |
| `.sir.json` | **Authoritative** canonical machine-verifiable signed receipt |
| `.pdf` | Optional human-readable presentation (receipt ID, key fingerprint, verification instructions, optionally embedded JSON) — not independently verified on its own |

Standard signature construction — a constrained **JWS JSON Serialization** profile with explicit canonicalisation and versioning. No proprietary signature algorithms.

Illustrative logical structure (final wire format to be specified — exact signed bytes, encoding, algorithm IDs):

```json
{
  "format": "secure-intake-receipt", "version": 1,
  "payload": {
    "receiptId": "rcpt_01J...", "submissionReference": "SI-8K4P2M",
    "organisationId": "org_123", "dropboxId": "dbx_456", "receivedAt": "2026-11-03T12:30:00Z",
    "commitment": { "algorithm": "SHA-256", "manifestDigest": "base64url-encoded-digest", "fileCount": 2 }
  },
  "signature": { "algorithm": "ES256", "keyId": "receipt-key-2026-01", "value": "base64url-encoded-signature" }
}
```

Every field's signed/unsigned status is decided explicitly.

## 3. What can and can't be established

**Can:** receipt unaltered since signing; signed by the private-key holder; identifies a specific submission and organisation; signer asserted receipt at the recorded time; commits to a specific immutable manifest; supplied ciphertext can be compared to committed digests.

**Can't (from the receipt alone):** uploader truthfulness; plaintext validity/legality; specific document contents; whether the organisation reviewed it; whether ciphertext is still available; independent trust in the timestamp. Product docs and verifier output state this explicitly.

## 4. Submission Evidence Manifest

The receipt signs a digest of a canonical manifest covering:

| Component | Commitment |
| --- | --- |
| Submission ID | Exact |
| Organisation, dropbox | Stable scoped IDs |
| Form snapshot | Immutable schema digest |
| Server-readable fields | Authenticated representation or protected commitment |
| E2EE form response | Ciphertext digest |
| Files | Ciphertext digests and lengths |
| File manifests | Digest, protocol, chunk info |
| Recipient envelopes | Digest of immutable envelope set |
| Recipient policy | Pinned version and key references |
| Revision number | Exact revision |
| Finalisation event | Receipt ID and timestamp binding |

```json
{ "manifestVersion": 1, "submissionId": "sub_123", "formSnapshotDigest": "sha256:...",
  "files": [ { "fileId": "file_001", "ciphertextLength": 104861600,
               "ciphertextDigest": "sha256:...", "encryptedManifestDigest": "sha256:..." } ],
  "encryptedFormPayload": { "ciphertextLength": 2048, "ciphertextDigest": "sha256:..." },
  "recipientEnvelopeSetDigest": "sha256:..." }
```

The manifest stays available for verification or is bundled with the receipt package (non-sensitive parts included for portability).

### Chunked files

- **Chunk commitment** — digest per confirmed ciphertext chunk.
- **File commitment** — digest of a canonical file manifest of ordered chunk digests, lengths and crypto context.

Verifiers stream chunks (no multi-GB concatenation). The canonical **file-manifest digest is authoritative**; a whole-ciphertext digest may be added. The format states which construction it uses (not the same as SHA-256 of concatenated bytes).

## 5. Signing keys

| Key | Purpose | Custodian |
| --- | --- | --- |
| File recipient key | Decrypt files | Reviewer/organisation |
| Draft encryption key | Recover unfinished drafts | Uploader |
| Audit checkpoint key | Sign audit checkpoints | Deployment security infrastructure |
| **Receipt signing key** | Attest accepted evidence | Deployment or delegated organisation signing service |

Models:

- **Deployment-managed signing** (recommended initially) — one authority; organisation identity in each receipt.
- **Organisation-managed signing** — dedicated per-organisation key, possibly in KMS/HSM (optional advanced).

Standalone: protected local key. Distributed: external signing service or managed key store. **Never silently generate a new signing identity on restart.**

**Algorithm:** **ES256** (ECDSA P-256/SHA-256) as the initial interoperability profile, if key providers and Native AOT support it. Future algorithms via explicit versioned profiles; clients can't choose or downgrade.

## 6. Public-key trust

A valid signature proves key possession, not who controls the key.

```text
GET /.well-known/secure-intake/receipt-keys.json
```

```json
{ "keyId": "receipt-key-2026-01", "algorithm": "ES256", "status": "active",
  "validFrom": "2026-11-01T00:00:00Z", "validUntil": null, "publicKeyFingerprint": "sha256:...",
  "publicKey": { "kty": "EC", "crv": "P-256", "x": "...", "y": "..." } }
```

Offline verification needs an independently obtained key: fingerprint pinning; locally installed trust registry; key file via trusted channel; certificate-backed identities later. **Never trust a key embedded in the receipt** (enables self-signed forgeries).

## 7. Rotation and revocation

`Pending → Active → Retired → Revoked/Compromised`. Retired keys verify history but can't sign.

Verifier distinguishes: signature mathematically valid; key trusted for the period; key later revoked/compromised; historical trust uncertain. A compromised key can forge plausible backdated receipts — without independent time evidence these can't all be distinguished, and rotation doesn't fix it. v3.5 keeps signed key-history records and audit checkpoints; **independent timestamp authorities are an optional later enhancement**.

## 8. Timestamps

| Field | Meaning |
| --- | --- |
| `receivedAt` | Server recorded successful **finalisation** (not session creation) |
| `signedAt` | Signing service created the receipt |
| `keyValidFrom` / `keyValidUntil` | Signing key's authorised period |

UTC, unambiguous ISO 8601; UI shows local time. Initially **server assertions** — monitor clock sync, flag/reject anomalies, never claim certified time without an external service.

## 9. Finalisation and signing consistency

DB commit and external signing can't be atomic, so two stages:

```text
Ciphertext verified
→ Transactional commit: submission finalised · evidence manifest immutable · receipt signing job created
→ Signing worker: sign canonical payload · store receipt · mark issued
→ Receipt available
```

A submission can be **received with receipt pending** (better than losing a submission to a KMS outage). The job is idempotent, operates on the immutable manifest digest (never recomputed from live data), and preserves **one authoritative receipt per submission revision**.

States: `received_receipt_pending`, `received_receipt_issued`, `receipt_signing_failed` (operational, not a failed submission). Public status stays **Received** while pending.

## 10. Independent verifier

No API access needed. Priority: **.NET 10 CLI**; optional desktop verifier; optional local browser verifier.

```bash
secureintake receipt verify --receipt submission.sir.json --trusted-key organisation-public-key.json
secureintake receipt verify --receipt submission.sir.json --trusted-key organisation-public-key.json \
  --evidence ./submission-evidence/
```

The first checks signature + embedded commitment; the second also checks supplied evidence. Reported as separate assurance levels (e.g. Signature: Valid · Signing key: Trusted locally · Manifest commitment: Matched · Ciphertext evidence: Not supplied).

Result codes:

```text
VALID_SIGNATURE_TRUSTED_KEY      VALID_SIGNATURE_UNTRUSTED_KEY    INVALID_SIGNATURE
UNSUPPORTED_RECEIPT_VERSION      UNSUPPORTED_SIGNATURE_ALGORITHM  SIGNING_KEY_REVOKED
SIGNING_KEY_STATUS_UNKNOWN       MANIFEST_DIGEST_MISMATCH         CIPHERTEXT_DIGEST_MISMATCH
EVIDENCE_NOT_SUPPLIED
```

A valid signature with an untrusted key is **never** a general "Verified".

```csharp
public interface IReceiptVerifier
{
    ValueTask<ReceiptVerificationResult> VerifyAsync(ReadOnlyMemory<byte> receipt, IReceiptTrustStore trustStore, CancellationToken ct = default);
}
public interface IReceiptTrustStore
{
    ValueTask<TrustedReceiptKey?> FindAsync(string issuer, string keyId, CancellationToken ct = default);
}
public sealed record TrustedReceiptKey(string Issuer, string KeyId, string Algorithm, byte[] SubjectPublicKeyInfo, ReceiptKeyTrustStatus Status);
public enum ReceiptKeyTrustStatus { Trusted, Retired, Revoked, Unknown }
public sealed record ReceiptVerificationResult(
    bool SignatureValid, bool KeyTrusted, bool ManifestValid, bool? CiphertextEvidenceValid, IReadOnlyList<string> Findings);
```

`CiphertextEvidenceValid` is nullable (evidence may not be supplied). Source-generated JSON; no reflection plugins; enforce receipt size, nesting depth, field-count and algorithm limits.

## 11. Distribution

| Method | Behaviour |
| --- | --- |
| Direct JSON download | Default; independently verifiable |
| PDF | Optional |
| Email notification | Says the receipt is ready |
| Return-access portal | While the capability is valid |
| Reusable uploader account | For explicitly linked submissions |
| Organisation API | Records management |

Retrieval never requires file access, but receipt URLs are protected (they reveal references, timestamps, organisational relationships). Names: `SI-8K4P2M-receipt.sir.json`, `SI-8K4P2M-receipt.pdf`; signature independent of filename. Download instructions include a verification command.

## 12. Privacy

Default receipts **exclude**: uploader email/phone; original filenames; server-readable answers; E2EE labels/values (unless strictly necessary); IP/device info; workflow states; reviewer identities; storage paths; recipient public keys. Use opaque IDs and commitments.

Hashes of low-entropy values leak via guessing — commit to server-readable data through a canonical **nonce-bound private evidence record**; keep the nonce with the private evidence package; the receipt holds only that record's digest.

## 13. Retention

| Resource | Retention |
| --- | --- |
| Signed receipt | Organisation evidence retention |
| Submission manifest | At least as long as evidence verification is supported |
| Ciphertext | Submission retention |
| Signing public keys | As long as receipts may be verified |
| Signing private keys | Only while authorised; securely retire |
| Key history and revocation | As long as trust must be assessed |
| Issuance audit | Audit retention |

Deleting ciphertext doesn't invalidate the signature, but prevents ciphertext comparison unless a copy exists. Legal holds preserve receipts and required evidence; an organisation can't delete the only manifest while claiming full verification remains possible. Live deletion ≠ backup deletion — document accurately.

## 14. Audit

```text
receipt.signing.requested | completed | failed
receipt.issued | downloaded | reissued
receipt.signing_key.created | activated | retired | revoked
receipt.verification.requested
receipt.evidence.exported
```

Offline verification creates no server event (and the system never implies it can monitor it).

**No silent replacement.** Corrections issue a new signed receipt referencing the previous ID with a reason; the original stays in evidence history.

## 15. Data model

```sql
CREATE TABLE submission_receipts (
    id UUID PRIMARY KEY,
    organisation_id UUID NOT NULL,
    submission_id UUID NOT NULL,
    submission_revision INTEGER NOT NULL,
    signing_key_id UUID NOT NULL,
    evidence_manifest_digest BYTEA NOT NULL,
    receipt_digest BYTEA,
    status VARCHAR(32) NOT NULL,
    received_at TIMESTAMPTZ NOT NULL,
    signed_at TIMESTAMPTZ,
    issued_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (organisation_id, submission_id, submission_revision)
);
```

```text
submission_evidence_manifests       receipt_signing_operations
submission_evidence_manifest_files  receipt_corrections
receipt_signing_keys                receipt_download_events
receipt_signing_key_history         receipt_evidence_exports
```

Signing key registry in the control plane; receipts and evidence in the tenant data plane; opaque cross-plane IDs.

## 16. API

```text
GET /api/public/submissions/{submissionId}/receipt         # requires submission return authorisation
GET /api/public/submissions/{submissionId}/receipt.pdf
GET  /api/organisations/{id}/submissions/{submissionId}/receipts
GET  /api/organisations/{id}/receipts/{receiptId}
POST /api/organisations/{id}/receipts/{receiptId}/evidence-export
GET  /.well-known/secure-intake/receipt-keys.json
```

No public lookup by predictable reference. Verification is local (optional online service later).

## 17. Security tests

Altered payloads; changed file digests; reordered manifests; removed/duplicated chunks; wrong organisation IDs; wrong/unknown/revoked keys; unsupported algorithms; malformed canonicalisation; truncated receipts; cross-tenant substitution; concurrent finalisation; signing-provider outage; rotation during issuance; historical verification after retirement. Fixed, independently generated test vectors. The verifier never depends on the API/DB to judge mathematical validity.

## 18. Implementation order

1. Canonical evidence manifest specification.
2. Chunk and file commitment format.
3. Versioned receipt payload specification.
4. Signing-key registry and custody abstraction.
5. Durable signing workflow.
6. JSON receipt download.
7. Independent .NET CLI verifier.
8. Trust-store and key-history verification.
9. PDF presentation.
10. Retention, correction and evidence-export workflows.
