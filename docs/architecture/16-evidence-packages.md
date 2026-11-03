# Portable Evidence Packages (`.siep`)

Source: notes v3.6. Import is in [17](17-evidence-import.md). Resolved questions: [QUESTIONS.md](../QUESTIONS.md) (Q-EVD-*). Planning decisions applied below take precedence over the source notes.

> **Planning decisions applied (2026-11-03)** — these override the text below where they differ. See [QUESTIONS.md](../QUESTIONS.md).
> - `.siep` export ships in **v1.1**. It's signed by a separate evidence key domain (Q-EVD-2). The workflow and notes extension is v1.3 (Q-EVD-1).

## Decision

Secure Intake supports **portable, encrypted evidence packages** that can be exported, archived, transferred and **independently verified without the original deployment**: the versioned **Secure Intake Evidence Package (`.siep`)**.

It's a transport/preservation format, **not a new encryption scheme** — it reuses existing encrypted files, confidential form payloads, recipient envelopes, authenticated manifests and signed receipts. **Exporting never requires the server to decrypt content.**

Export, verification and decryption are **separate capabilities** — a user may export encrypted evidence without holding decryption keys.

## 1. Architecture

```text
Finalised submission (ciphertext · form snapshot · envelopes · signed receipt)
→ Authorised export (permission checks · approval policy · immutable evidence snapshot)
→ Streaming package writer (manifest · receipt · ciphertext streams · envelope references)
→ Portable .siep (immutable evidence · integrity commitments · no plaintext files)
→ Offline verification (trust · signature · hashes · completeness)
→ Authorised decryption (reviewer-controlled keys · local plaintext)
```

## 2. Container: strict ZIP64 profile

Chosen for mature tooling, streaming read/write, large-file support, no full-RAM loading, inspectability and .NET 10 integration. The spec defines a **strict profile** — no archive-level encryption, arbitrary compression, path traversal or duplicate entries. Ciphertext doesn't compress, so compression adds little.

```text
submission-SI-8K4P2M.siep
├── package.json
├── package-signature.jws
├── evidence/
│   ├── submission-manifest.json
│   ├── form-snapshot.json
│   ├── readable-fields.enc.json
│   └── recipient-envelope-manifest.json
├── receipts/submission-revision-1.sir.json
├── encrypted/
│   ├── form-response.bin
│   └── files/
│       ├── file-001/{manifest.json, chunk-000000.bin, chunk-000001.bin}
│       └── file-002/{manifest.json, chunk-000000.bin}
├── envelopes/{file-001.json, file-002.json, form-response.json}
└── trust/{signing-key.json, key-history.json}     # public material only — NOT automatically trusted
```

## 3. Package manifest

```json
{
  "format": "secure-intake-evidence-package", "version": 1, "packageId": "pkg_01J...",
  "organisationId": "org_123", "submissionId": "sub_456", "submissionReference": "SI-8K4P2M",
  "submissionRevision": 1, "createdAt": "2026-11-03T13:00:00Z", "receiptId": "rcpt_789",
  "evidenceManifestDigest": "sha256:...",
  "entries": [
    { "path": "evidence/submission-manifest.json", "length": 4096, "digest": "sha256:..." },
    { "path": "encrypted/files/file-001/chunk-000000.bin", "length": 4194320, "digest": "sha256:..." }
  ]
}
```

Normalised relative paths; reject duplicates, traversal, symlinks, unexpected entry types and unsupported compression. Canonicalised before signing.

## 4. Two independent signatures

| Signature | Issuer | Purpose |
| --- | --- | --- |
| Submission receipt | Original signing authority | Acceptance of the committed submission |
| Export package signature | Exporting deployment's signing authority | Which entries were assembled for export |

The package signature never replaces the receipt (exports may happen years later, possibly from a different deployment after migration). Modified ciphertext fails package integrity (and receipt evidence where committed); removed entries fail completeness.

## 5. Confidentiality and export profiles

Never plaintext E2EE files or form values. Server-readable values aren't exported in plaintext by default.

| Profile | Contents |
| --- | --- |
| **`minimal`** (default) | Ciphertext, required manifests, envelopes, signed receipts, minimal public-safe metadata |
| `complete` | Plus server-readable values inside an **export-encrypted** metadata payload |

Complete exports use a separate export-metadata DEK wrapped for explicitly approved recipient keys — **export-time encryption, not historical E2EE** (the server saw those values), and the package metadata says so. Original file envelopes are never modified. The manifest records which components are export-encrypted vs visible.

## 6. Recipient envelopes

Preserve the original envelope set for the revision: recipient key IDs; suite IDs; envelope ciphertext; key epochs; recipient policy version; envelope creation metadata; authenticated envelope-set digest. Never private keys, recovery passwords or unwrapped DEKs.

- Exporting an envelope doesn't mean anyone can decrypt it; retired/deleted keys may make a package permanently undecryptable. Preflight reports key availability where inventory is reliable, without claiming to prove off-platform key possession.
- **The server can't add new recipients during export.** New access uses the client-side rewrap workflow ([09](09-key-management-and-recipients.md)), creating a new envelope-set version with audit evidence; original commitments are preserved, not rewritten.

## 7. Streaming export

Read ciphertext from storage (S3/R2/local) → export worker with bounded buffers → ZIP64 output stream. Each entry is streamed, hashed and checked against the immutable evidence manifest. Constant memory relative to file size.

```csharp
public interface IEvidencePackageExporter
{
    Task<ExportResult> ExportAsync(EvidenceExportRequest request, Stream destination, CancellationToken ct = default);
}
public sealed record EvidenceExportRequest(
    Guid OrganisationId, Guid SubmissionId, int SubmissionRevision,
    EvidenceExportProfile Profile, Guid RequestedBy, string IdempotencyKey);
public enum EvidenceExportProfile { Minimal, Complete }
public sealed record ExportResult(Guid PackageId, long BytesWritten, byte[] PackageDigest, DateTimeOffset CompletedAt);
```

Never accept caller-supplied storage paths; resolve immutable references via the tenant storage abstraction.

## 8. Consistency

```text
Authorise → select revision → resolve immutable evidence manifest → create export snapshot
→ pin object references → stream and verify → create package manifest → sign → publish artifact
```

The snapshot fixes the exact envelope-set version (no mid-export switching). Use immutable object versions or content addressing; if an object changes/disappears, **fail** — never substitute a same-named object.

## 9. Job lifecycle

Small packages may stream directly (if policy allows); large ones use durable jobs:

```text
Requested → Authorising → Preparing → Exporting → Verifying → Signing → Ready → Expired/Deleted
failures: authorisation_failed · approval_required · source_unavailable · integrity_failed · signing_failed · cancelled
```

A failed export is never published as valid.

## 10. Authorisation

```text
submissions.evidence.export            submissions.evidence.export.bulk
submissions.evidence.export.complete   submissions.evidence.export.approve
                                       submissions.evidence.export.audit
```

Independent of decryption authorisation.

| Operation | Default control |
| --- | --- |
| Single minimal export | Authorised reviewer or designated exporter |
| Complete export | Elevated permission |
| Bulk export | Elevated permission + approval |
| Including sensitive server-readable metadata | Explicit policy |
| To external destination | Approval where configured |
| Under legal hold | Retention/security policy evaluation |

High-assurance: two-person approval, fresh MFA, no self-approval ([22](22-access-control.md)); approval binds to an immutable manifest of exactly what's exported.

## 11. Offline verification

```bash
secureintake evidence verify --package submission-SI-8K4P2M.siep \
  --trusted-key receipt-public-key.json --trusted-export-key export-public-key.json
```

1. ZIP64 structural validation. 2. Package manifest validation. 3. Entry count and path validation. 4. Entry length and digest verification. 5. Package signature. 6. Signing-key trust evaluation. 7. Receipt signature. 8. Evidence manifest. 9. Ciphertext commitments. 10. Recipient-envelope integrity.

No network needed; machine-readable JSON output. Results reported per area (structure, package signature, receipt, ciphertext commitments, trusted keys, "Plaintext verification: Not performed") with the caveat that it doesn't establish plaintext contents or uploader identity.

### Verify vs decrypt (separate commands)

```bash
secureintake evidence verify  --package submission.siep --trusted-key receipt-public-key.json
secureintake evidence decrypt --package submission.siep --key reviewer.sik --output ./decrypted/
```

Decryption requires private-key access; verifies integrity and authenticated encryption metadata first; never overwrites without confirmation; sanitises untrusted output filenames; writes only to a user-selected destination with appropriate permissions.

## 12. Archive safety

Prohibit: absolute paths; `../`; symlinks; duplicate names; unsupported compression; unbounded metadata; excessive entry counts; automatic nested-archive processing; executable extraction hooks; external references. Bounded memory and configurable limits — `.siep` files may come from untrusted third parties. The exporter writes only known, validated entry types.

## 13. Retention

| Resource | Retention |
| --- | --- |
| Original ciphertext | Submission policy |
| Signed receipt | Evidence retention |
| Generated export package | Export retention (short default, e.g. **24 h**) |
| Export job metadata | Operational retention |
| Export audit | Audit retention |
| Downloaded package | Outside platform control |

On expiry: revoke download access; delete temporary objects; verify/retry cleanup; keep metadata and audit per policy. Deleting the server copy can't delete downloaded packages — state this in UI and docs.

## 14. Migration and provenance

| Operation | Behaviour |
| --- | --- |
| Verify | Validate offline |
| Archive | Store as external evidence |
| Import evidence | Register package and provenance |
| Restore submission | Reconstruct an eligible record under controlled migration |
| Re-encrypt | Client-side transformation |

The destination never rewrites the original receipt; it may sign a separate **import attestation**, keep original reference/issuer/receipt as provenance, and assign its own internal IDs. Private keys don't migrate — new recipients need rewrap/re-encryption. Matching display names never imply the same legal entity. Details in [17](17-evidence-import.md).

```json
{ "importId": "imp_123", "sourcePackageDigest": "sha256:...", "sourceReceiptId": "rcpt_456",
  "destinationOrganisationId": "org_789", "importedAt": "2026-11-03T15:00:00Z",
  "verificationResult": "valid", "importMode": "archive" }
```

## 15. Audit events

```text
evidence.export.requested | approved | denied | started | completed | failed | downloaded | expired | deleted
evidence.import.requested | verified | completed | rejected
evidence.package.signature.created | integrity_failed
```

Include actor, organisation, submission and revision, profile, approval reference, package digest, signing key ID, destination classification, outcome and timestamp. Never plaintext E2EE values, private keys or decrypted content.

## 16. Data model

```text
evidence_export_jobs              evidence_export_cleanup_operations
evidence_export_job_items         evidence_import_jobs
evidence_export_snapshots         evidence_import_attestations
evidence_export_artifacts         evidence_import_provenance
evidence_export_approvals         evidence_package_signing_keys
evidence_export_download_grants   evidence_package_signing_operations
```

Integrated with tenant routing, audit and durable workers; artifacts via the storage abstraction with separate prefixes/buckets where appropriate.

## 17. API

```text
POST /api/organisations/{id}/submissions/{submissionId}/evidence-exports    # Idempotency-Key
GET  /api/organisations/{id}/evidence-exports/{exportId}
GET  /api/organisations/{id}/evidence-exports/{exportId}/download           # short-lived scoped capability
POST /api/organisations/{id}/evidence-exports/{exportId}/cancel
POST /api/organisations/{id}/evidence-imports
GET  /api/organisations/{id}/evidence-imports/{importId}
```

Eligibility is revalidated before issuing a download capability (permissions or organisation status may have changed).

## 18. Implementation order

1. Formal `.siep` specification.
2. Canonical package manifest.
3. ZIP64 streaming writer.
4. Ciphertext digest verification during export.
5. Package signing.
6. Offline CLI verifier.
7. Export authorisation and approval integration.
8. Temporary artifact storage and cleanup.
9. Encrypted server-readable metadata export.
10. Import and provenance support.
