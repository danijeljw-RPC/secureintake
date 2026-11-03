# Evidence Import and Native Restoration

Source: notes v3.7. Builds on [16](16-evidence-packages.md) (`.siep`), [15](15-receipts.md) (receipts), [09](09-key-management-and-recipients.md) (recipients) and [22](22-access-control.md) (approvals). Resolved questions: [QUESTIONS.md](../QUESTIONS.md) (Q-IMP-*). Planning decisions applied below take precedence over the source notes.

> **Planning decisions applied (2026-11-03)** — these override the text below where they differ. See [QUESTIONS.md](../QUESTIONS.md).
> - Import ships in **v1.2** (archive-only first). Foreign issuer trust is a manual fingerprint, confirmed out of band, with JIT and Security Officer approval (Q-IMP-1).

## Decision

Two import modes:

1. **Evidence archive** — verify and retain a `.siep` as **read-only evidence**, no live submission.
2. **Native restoration** — restore a compatible submission into the destination, preserving original cryptographic evidence, receipt and provenance.

Native restoration is a **controlled administrative operation** and never silently treated as a newly received submission.

> Imported evidence can be verified and restored, but must never acquire a new, misleading origin. The original receipt stays authoritative for the original submission; the destination attestation records what happened during import.

## 1. Architecture

```text
Incoming .siep (ciphertext · manifests · envelopes · signed receipt · public-key evidence)
→ Quarantine + offline verification (archive safety · digests · signatures · trust registry · completeness)
→ Import planning (tenant mapping · duplicate detection · key availability · compatibility · approval)
     ├── Archive-only: immutable evidence record, no workflow execution
     └── Native restoration: restored submission, isolated workflow, provenance retained
→ Destination attestation + reconciliation (signed import record · object verification · audit · status)
```

A restored submission is a **new destination-side record linked to original evidence**, not a rewrite of history.

## 2. Modes

| Capability | Archive-only | Native restoration |
| --- | --- | --- |
| Verify signatures and hashes | Yes | Yes |
| Retain original package | Yes | Yes |
| Create native submission | No | Yes |
| Preserve original receipt | Yes | Yes |
| Reviewer UI access | Evidence viewer | Restored submission view |
| Trigger normal workflow | No | **Not automatically** |
| Decrypt with existing recipient keys | If available | If available |
| Destination attestation | Yes | Yes |
| Delete/alter source evidence | Never | Never |

Both require an **explicit** destination organisation — never inferred from untrusted package fields.

## 3. Trust boundaries

Three separate decisions:

1. **Evidence integrity** — does the package match its signed manifests and receipts?
2. **Issuer trust** — does the destination trust the original receipt issuer?
3. **Destination authorisation** — may this evidence be imported into this organisation, and how?

An authentic package may still belong to another organisation or contain evidence the destination isn't entitled to hold.

```json
{
  "trustedIssuers": [
    { "issuerId": "issuer_original", "keyFingerprints": ["sha256:..."],
      "allowedSourceOrganisations": ["org_original"],
      "allowArchiveImport": true, "allowNativeRestoration": false }
  ],
  "requireValidReceipt": true, "requireValidPackageSignature": true,
  "allowUnknownIssuer": false, "requireApprovalForNativeRestoration": true
}
```

Unknown issuers may be quarantined for investigation, never auto-trusted. Keys inside `.siep` are evidence, not trust anchors.

## 4. Verification

Uses the **same verification library** as the offline CLI.

| Stage | Validation |
| --- | --- |
| Container | Valid constrained ZIP64 profile |
| Entries | No traversal, duplicates or unsupported types |
| Package manifest | Canonical structure, required entries, limits |
| Package signature | Cryptographic validity |
| Source trust | Trusted key and issuer policy |
| Submission receipt | Valid original signature |
| Evidence manifest | Matches receipt commitment |
| Ciphertext | Every required object matches its committed digest |
| Envelopes | Original envelope-set digest matches |
| Schema | Supported form and crypto protocol versions |
| Tenant | Explicitly authorised source→destination mapping |

Produces a signed or audit-bound report stating exactly what passed, failed or couldn't be completed. **Native restoration requires complete integrity verification**; partially verified packages may stay quarantined but never become native submissions.

## 5. Lifecycle

```text
Uploaded (quarantine) → Verifying → Verified → Planning → Awaiting approval
→ Restoring → Reconciling → Completed
exceptional: rejected · cancelled · failed · quarantined · cleanup_pending
```

A failed restoration never leaves a partially visible native submission.

## 6. Provenance

```text
Original deployment: Submission SUB-A, Receipt RCPT-A
  → Export package PKG-A
  → Destination: Import IMP-B, Native submission SUB-B, Import attestation ATT-B
```

Preserve: original issuer; original organisation and dropbox IDs; original submission ID and reference; original receipt and digest; package digest; verification report digest; destination organisation and submission IDs; import actor and approval reference; import time; mode.

```json
{ "source": { "issuerId": "issuer_original", "organisationId": "org_original", "submissionId": "sub_original",
              "receiptId": "rcpt_original", "packageDigest": "sha256:..." },
  "destination": { "organisationId": "org_destination", "submissionId": "sub_restored", "importId": "imp_123" },
  "importedAt": "2026-11-03T15:00:00Z", "mode": "native_restoration" }
```

Never replace the original receipt; issue a separate **signed import attestation**.

## 7. Native restoration

```csharp
public enum SubmissionOrigin { PublicUpload, ApiUpload, ImportedEvidence, DisasterRecovery }

public interface ISubmissionRestorationService
{
    Task<RestorationResult> RestoreAsync(RestorationRequest request, CancellationToken ct = default);
}
public sealed record RestorationRequest(
    Guid ImportId, Guid DestinationOrganisationId, Guid RequestedBy, Guid ApprovedOperationId, string IdempotencyKey);
public sealed record RestorationResult(Guid DestinationSubmissionId, Guid ImportAttestationId, RestorationStatus Status);
public enum RestorationStatus { Restoring, Reconciling, Completed, Failed }
```

- Separate auditable pipeline — **never** the public-upload finalisation endpoint.
- Restoration-specific state **"Restored — Pending Review"** (not an ordinary workflow state). A workflow is assigned only by explicit admin action.
- **No automatic notifications, webhooks, assignment rules or approval requests** fire from restoring historical evidence.

## 8. Workflow compatibility

Original workflow history is preserved as evidence (if exported) but never executed.

| Strategy | Behaviour |
| --- | --- |
| Archive only | No destination workflow |
| Assign default workflow | Explicit admin activation |
| Map compatible workflow | Validated, versioned, reviewed state/transition mapping |
| Keep pending | No processing until resolved |

The current `.siep` spec doesn't include internal notes, workflow approvals, escalation history or operational audit — that needs a separate export extension. **The importer never invents missing history.**

## 9. Key availability

Integrity and decryptability are independent.

| State | Meaning |
| --- | --- |
| `recipient_key_known` | Public key recognised |
| `recipient_key_unknown` | Not registered locally |
| `private_key_available` | Authorised client demonstrated usable possession |
| `rewrap_required` | Destination reviewer lacks an applicable envelope |
| `recovery_required` | Organisation recovery may be needed |
| `unrecoverable` | No usable decryption path known |

A known public key never implies private-key availability. Proof-of-possession establishes key control, not that every file decrypts; high-assurance restoration may have the client decrypt a sample or all evidence locally and report the result (server never receives plaintext).

### Post-restoration recipient changes

1. Identify an authorised holder of an existing recipient key. 2. Obtain approval. 3. Client-side DEK rewrap. 4. New authenticated envelope-set version. 5. Link to the original envelope set. 6. Original receipt and manifest unchanged.

This is a post-import access modification, not a correction. Without an authorised key or recovery path, plaintext is unrecoverable.

## 10. Disaster recovery scenarios

- **A. Same-deployment recovery** — restore coordinated DB and storage backups, keeping deployment identity, key registry and tenant config ([18](18-backup-and-recovery.md)).
- **B. New-deployment restoration** — import verified `.siep` packages into a new deployment, preserving provenance.
- **C. Selective evidence recovery** — restore individual submissions from archives without restoring the whole source DB.

**`.siep` is not a replacement for database and configuration backups** — it lacks credentials, organisation settings, workflow config, queues, notification state and complete audit history.

Recovery objectives (illustrative planning targets, not guarantees): RPO 15 min; RTO 4 h; package verification before restoration; key availability check before activation; reconciliation before completion. Lost signing keys still allow historical verification with trusted public keys, but no new receipts under that identity.

## 11. Reconciliation

| Check | Expected |
| --- | --- |
| Imported file count | Matches package manifest |
| Ciphertext object count | Matches expected storage representation |
| Ciphertext digests | Match original commitments |
| Envelope-set digest | Matches selected version |
| Original receipt | Preserved **byte-for-byte** |
| Destination submission | Correct tenant and origin |
| Retention policy | Explicitly assigned |
| Legal hold | Preserved or explicitly mapped |
| Storage quota | Correctly accounted |
| Import attestation | Signed and persisted |

Repeatable, via a resumable durable worker. Ciphertext goes to **staging** storage first, then is promoted/registered as immutable destination objects after digest verification; failed promotion leaves the import incomplete and staging is cleaned up.

## 12. Duplicate detection

Key: source issuer + original submission ID + original revision + original receipt digest + package digest (package digest alone is insufficient — the same evidence can be packaged repeatedly).

Policies: `reject` · `link_existing` · `create_separate_archive_copy`. Native restoration defaults to **reject** unless it's an idempotent retry of the same restoration. Never create multiple native submissions for the same original evidence.

## 13. Retention and legal holds

Never inherit source retention blindly. An **explicit destination retention policy** is required; source retention metadata is kept as provenance.

Active source legal holds require a policy decision during native restoration (never silently discarded). Same-organisation DR: preserve equivalent holds by default. Cross-organisation transfer: explicit approval and documented responsibility.

## 14. Permissions and approval

```text
evidence.import.create    evidence.import.restore        evidence.import.audit
evidence.import.verify    evidence.import.approve
evidence.import.archive   evidence.import.manage_trust
```

Native restoration needs elevated permission. Managing trusted issuers is a separate security-admin capability; uploading a package never lets a user trust its key.

Approval (sensitive-operation engine) binds to an immutable plan: package digest; source issuer and organisation; destination organisation; mode; selected revisions; retention policy; key availability assessment; intended workflow state. Material changes invalidate approval.

## 15. API (multi-step)

```text
POST /api/organisations/{id}/evidence-imports
GET  /api/organisations/{id}/evidence-imports/{importId}
POST /api/organisations/{id}/evidence-imports/{importId}/verify
POST /api/organisations/{id}/evidence-imports/{importId}/plan
POST /api/organisations/{id}/evidence-imports/{importId}/approve
POST /api/organisations/{id}/evidence-imports/{importId}/execute
GET  /api/organisations/{id}/evidence-imports/{importId}/reconciliation
```

`execute` requires a verified, approved, unchanged plan. Optimistic concurrency and idempotency keys throughout.

```json
{ "importId": "imp_123", "packageDigest": "sha256:...", "mode": "native_restoration",
  "destinationOrganisationId": "org_destination",
  "source": { "issuerId": "issuer_original", "organisationId": "org_original", "submissionId": "sub_original" },
  "verification": { "packageValid": true, "receiptValid": true, "issuerTrusted": true, "ciphertextComplete": true },
  "restoration": { "workflowMode": "pending_assignment", "retentionPolicyId": "ret_456",
                   "preserveOriginalReceipt": true, "issueImportAttestation": true },
  "requiresApproval": true }
```

Stored immutably once submitted for approval.

## 16. Data model

```text
evidence_import_jobs                  evidence_import_object_mappings
evidence_import_packages              evidence_import_reconciliation_runs
evidence_import_verification_reports  evidence_import_reconciliation_findings
evidence_import_plans                 evidence_import_attestations
evidence_import_approvals             evidence_import_trust_decisions
evidence_import_source_mappings       evidence_import_cleanup_operations
evidence_import_submission_mappings
```

`submissions` gains: `origin_type, origin_import_id, original_issuer_id, original_submission_id, original_receipt_digest, restoration_status` — immutable after completion except via authorised correction workflows.

## 17. Audit events

```text
evidence.import.created | package_uploaded
evidence.import.verification_started | verification_completed | verification_failed
evidence.import.plan_created | plan_approved | plan_rejected
evidence.import.restoration_started | restoration_completed | restoration_failed
evidence.import.reconciliation_completed | reconciliation_failed
evidence.import.attestation_issued
evidence.import.trust_policy_changed
```

Include package digest, destination organisation, actor, approval reference, verification outcome, restoration IDs. Never private keys or decrypted content. External offline verification isn't recorded unless a report is submitted.

## 18. UI

Desktop-only import workflow: package integrity, issuer trust, ciphertext file count, private-key access ("Not confirmed"), mode and approval status, destination, workflow handling. Native restoration and trust-policy management are desktop-only.

## 19. Implementation order

1. Quarantined package ingestion.
2. Shared offline/online verification library.
3. Trusted issuer policy and key-history validation.
4. Archive-only import.
5. Immutable import plans.
6. Approval-controlled native restoration.
7. Ciphertext staging and object reconciliation.
8. Signed destination import attestations.
9. Recipient-key availability assessment.
10. DR runbooks and restoration testing.
