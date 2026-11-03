# Backup and Recovery

Source: notes v3.8. Resolved questions: [QUESTIONS.md](../QUESTIONS.md) (Q-BAK-*). Planning decisions applied below take precedence over the source notes.

> **Planning decisions applied (2026-11-03)** — these override the text below where they differ. See [QUESTIONS.md](../QUESTIONS.md).
> - Break-glass uses FIDO2 emergency credentials through the host CLI, with 2 custodians in Hardened and a signed, hash-chained emergency journal (Q-BAK-1).
> - Restore drills: a readiness warning after 180 days without a drill (Hardened: 90). Not enforced (Q-BAK-2).

## Decision

Secure Intake **does not** run a backup scheduler, backup-storage service or automatic DR system. It provides **backup-aware operational controls**: consistency checkpoints, maintenance fencing, restore validation, reconciliation and recovery commands. The operator schedules, encrypts, retains, replicates and restores backups.

- Secure Intake guarantees application-level consistency checks and recovery-safe behaviour within documented boundaries.
- The operator owns backup infrastructure and its recoverability.
- A database backup alone is never assumed to be a complete recoverable deployment.

## 1. Architecture

```text
Running deployment (control plane · tenant DBs · ciphertext storage · key references · audit)
  → Application consistency checkpoint (readiness · object inventory · schema versions · manifest)
  → Operator backup tools (DB snapshots/PITR · object versioning · encrypted copies · key custody)
  → Operator backup destination (local/offsite · immutable · retention · replication)
  → Secure Intake recovery validation (DB/schema · ciphertext inventory · key registry · audit continuity · report)
```

## 2. What must be backed up

| Component | Responsible | Recovery significance |
| --- | --- | --- |
| Control-plane database | Operator | Organisations, routing, configuration, identity references |
| Tenant databases | Operator | Submissions, workflows, envelopes, receipts, audit |
| Ciphertext object storage | Operator | Encrypted files and form payloads |
| Deployment configuration | Operator | Storage profiles, auth, routing, integrations |
| Signing keys and secret custody | Operator / key custodian | Receipt issuance, service operation, trust continuity |
| Trust registries and audit checkpoints | Operator | Historical verification and tamper detection |

Also preserve migration history and application/protocol versions.

**Reviewer private keys are not in platform backups.** Organisation key custody, portable `.sik` backups and external KMS/HSM recovery are separate ([09](09-key-management-and-recipients.md)). A perfect restore can still contain permanently undecryptable submissions if keys were lost.

## 3. Database / object-storage consistency

A submission spans session creation, object uploads, object confirmation, finalisation, receipt issuance and audit/notification events — not one transaction. Restoring the DB to 12:00 and storage to 11:55 leaves dangling references; storage at 12:05 leaves unreferenced objects.

Rules:

- Finalised submissions reference **immutable** ciphertext objects; object IDs are never reused.
- Object deletion is delayed until retention and reconciliation conditions are met.
- The DB records expected object digests and lengths.
- Recovery compares DB references with actual objects. Unreferenced objects are quarantined/reported, **never auto-deleted** during recovery. Missing referenced objects are **recovery failures**.

Preferred relationship: restored storage contains every object referenced by the restored DB.

## 4. Recovery Consistency Manifest

Application-generated metadata to assess a backup set — not a backup itself.

```json
{
  "format": "secure-intake-recovery-checkpoint",
  "version": 1,
  "checkpointId": "chk_01J...",
  "deploymentId": "dep_123",
  "createdAt": "2026-11-03T12:00:00Z",
  "applicationVersion": "3.8.0",
  "schemaVersions": { "controlPlane": 18, "tenantData": 27 },
  "tenants": [ { "organisationId": "org_123", "databaseProfileId": "dbprofile_1", "storageProfileId": "storage_1" } ],
  "auditCheckpointId": "audit_chk_456"
}
```

Also records the storage consistency boundary, DB recovery positions and object inventory commitments where available. A checkpoint made before a backup does not by itself make later DB and storage backups consistent; the operator follows a documented sequence or uses coordinated snapshots/PITR with object-version retention.

## 5. Backup coordination modes

| Mode | Behaviour | Fit |
| --- | --- | --- |
| Quiesced | Stop writes, checkpoint, back up | Small standalone (SQLite/local FS) |
| Online coordinated | Keep running; WAL/PITR + immutable object versions | Production PostgreSQL |
| Storage-first protected | Preserve required object versions before restoring DB state | Distributed deployments |

**Quiesced:** enter maintenance → stop uploads/finalisation → drain/fence writers → DB-consistent snapshot (SQLite backup API or coordinated FS snapshot — never copy a live DB file) → capture matching object state → record manifest → resume.

**Online:** PostgreSQL PITR tooling; retain immutable object versions; preserve objects needed by the chosen recovery point; record DB positions and inventory boundaries; validate before reopening.

## 6. Maintenance mode and fencing

Durable deployment-level write fencing, not a UI flag. During quiesced backup:

- New public upload sessions rejected; existing sessions paused or drained per policy.
- Finalisation blocked after the fence.
- Workers stop mutating protected state; cleanup and retention deletion suspended.
- Administrative writes blocked except approved maintenance operations.
- Multi-node: enforced via shared authoritative state; a node that loses contact **fails closed**.
- Issued presigned URLs remain usable until expiry — wait them out or use provider mechanisms. Unfinalised ciphertext is never committed evidence.

## 7. Commands

These coordinate application state and produce verification metadata; they **do not create backups**.

```bash
secureintake maintenance enter --reason "scheduled-backup"
secureintake backup checkpoint create --output ./recovery-checkpoint.json
secureintake backup preflight --checkpoint ./recovery-checkpoint.json
secureintake maintenance exit

secureintake restore validate  --checkpoint ./recovery-checkpoint.json
secureintake restore reconcile --checkpoint ./recovery-checkpoint.json --verify-objects
```

Example skeleton (checkpoint/preflight only — a quiesced backup must also set/release the fence; online must use a documented PITR/object-version protocol):

```bash
#!/usr/bin/env bash
set -euo pipefail
CHECKPOINT_DIR="/var/backups/secureintake/checkpoints"
mkdir -p "$CHECKPOINT_DIR"
CHECKPOINT="$CHECKPOINT_DIR/$(date -u +%Y%m%dT%H%M%SZ).json"
secureintake backup checkpoint create --output "$CHECKPOINT"
secureintake backup preflight --checkpoint "$CHECKPOINT"
# Invoke the operator's database and storage backup tools here.
```

## 8. Encrypted backups

Operators must encrypt backups independently of E2EE, because databases hold non-E2EE data: organisation configuration, server-readable form fields, contact details, submission metadata, workflow records, audit, auth/integration references.

Use authenticated encryption with operator-controlled keys (external KMS/HSM, age-compatible tools, established backup products). No vendor is mandated. Backup decryption secrets are never stored in the same backup set.

## 9. Signing-key custody

| Key | Recovery requirement |
| --- | --- |
| Receipt signing private key | Continue signing under the same identity |
| Receipt signing public key | Historical verification |
| Audit checkpoint private key | Continue checkpoint signing under that identity |
| Audit checkpoint public key | Historical chain verification |
| Deployment secret keys | Service continuity; decrypting server-managed secrets |
| Reviewer private keys | Outside platform backup |

- Lost receipt key → activate a new key; history stays verifiable if public keys and trust history survive.
- Suspected compromise → rotation isn't enough; mark compromised and assess historical trust.
- Lost server-managed secret-encryption key → some stored integration credentials/encrypted metadata become inaccessible.
- Restore preflight identifies missing key dependencies before activation.

## 10. Scheduling (operator-owned, illustrative)

| Component | Example frequency |
| --- | --- |
| PostgreSQL base backup | Daily |
| WAL archiving | Continuous |
| Object storage replication/backup | Continuous or frequent |
| Configuration backup | On change + daily |
| Signing-key metadata | On change |
| Recovery checkpoint | Aligned with backup |
| Restore drill | Quarterly |

Operators may use cron, systemd timers, Kubernetes CronJobs, cloud backup services, etc. These are not application defaults or guarantees.

## 11. Restore flow

A restored deployment starts in **recovery mode**, not serving public traffic:

```text
Restored → Recovery validation → Reconciliation → Security review → Operational approval → Active
```

| Area | Validation |
| --- | --- |
| Database | Integrity, schema compatibility |
| Tenant routing | Correct organisation → database mapping |
| Storage | Referenced objects exist |
| Ciphertext | Digests match committed manifests |
| Signing keys | Required keys and trust history available |
| Receipts | Signatures verifiable |
| Audit | Chain and checkpoint continuity |
| Retention | Holds and deletion state preserved |
| Jobs | No unsafe duplicate processing |
| Webhooks | Replay policy reviewed |
| Identity | Sessions and credentials handled safely |
| Configuration | Secrets, domains, endpoints validated |

Stays fenced until mandatory validation passes or an authorised recovery exception is recorded.

### Job and outbox recovery

Restores can resurrect completed work as pending (e.g. a webhook delivered after the backup point). Reset stale leases; reconcile incomplete operations; preserve idempotency keys; never regenerate conflicting signed receipts; recheck external side effects before replay; require explicit policy for high-impact webhook replay; record recovery-originated retries. Exactly-once external delivery is impossible — consumers dedupe on stable event IDs.

## 12. RPO / RTO

- **RPO** depends on WAL archive frequency, object replication lag, backup completion time, key/config freshness, IdP availability, object version retention. A DB recovery point is only useful if all referenced ciphertext is recoverable.
- **RTO** depends on DB and object restore duration, secret/key availability, infrastructure provisioning, routing restoration, reconciliation, security validation and approval.

Declared operator targets for reporting and validation — never enforced guarantees:

```json
{ "recoveryObjectives": { "rpoMinutes": 15, "rtoMinutes": 240 } }
```

## 13. Retention and immutable backups

| Class | Purpose |
| --- | --- |
| Short-term | Operational recovery |
| Medium-term | Accidental deletion and corruption |
| Long-term | Compliance and archival |
| Immutable/offsite | Ransomware and admin-compromise resilience |

Production application credentials must not be able to delete or overwrite all backups. Deleted submissions may persist in backups until they expire — document this; never claim immediate erasure across all media.

## 14. Recovery drills

Required in production guidance. Checklist:

- [ ] Restore control-plane database
- [ ] Restore tenant database
- [ ] Restore ciphertext objects
- [ ] Validate tenant routing
- [ ] Verify sample signed receipts
- [ ] Reconcile ciphertext digests
- [ ] Validate signing-key trust history
- [ ] Check legal holds and retention
- [ ] Review pending jobs and webhook replay
- [ ] Confirm reviewer decryption from an authorised client
- [ ] Record achieved RPO and RTO
- [ ] Approve recovery report

The drill report records actual recovery point, elapsed time, verification failures and unresolved risks.

## 15. Recovery epochs and audit continuity

Restoring an older DB removes newer audit records; externally stored signed checkpoints reveal the rollback. Compare them and start a **new recovery epoch** rather than silently continuing the chain.

```text
deployment_recovery_epochs(deployment_id, recovery_epoch, recovered_from_checkpoint, activated_at, approved_by)
```

Each DR activation increments the epoch; new audit records, operational events and receipt-signing metadata reference it.

## 16. Audit events

```text
recovery.checkpoint.created | validated
recovery.maintenance.entered | exited
recovery.validation.started | completed | failed
recovery.reconciliation.started | completed | failed
recovery.activation.requested | approved | completed
recovery.drill.started | completed
```

Secure Intake can't audit external backup tools unless the operator supplies results; it must not claim an offsite backup succeeded without evidence.

## 17. Data model

```text
recovery_checkpoints                  recovery_validation_findings
recovery_checkpoint_tenants           recovery_reconciliation_runs
recovery_checkpoint_storage_profiles  recovery_reconciliation_findings
deployment_recovery_epochs            recovery_activation_approvals
recovery_validation_runs              recovery_drill_reports
```

For consistency, validation and audit only — the backup catalogue stays outside Secure Intake.

## 18. API

```text
POST /api/platform/recovery/checkpoints
GET  /api/platform/recovery/checkpoints/{id}
POST /api/platform/recovery/validate
GET  /api/platform/recovery/validations/{id}
POST /api/platform/recovery/reconcile
GET  /api/platform/recovery/reconciliations/{id}
POST /api/platform/recovery/activate
```

Activation needs elevated permission and fresh MFA, plus independent approval where configured. If normal auth is unavailable, an operator-controlled **break-glass CLI** procedure may be needed — it must not skip audit or allow unrestricted plaintext access.

## 19. Implementation order

1. Immutable ciphertext and object-reference recovery guarantees.
2. Recovery consistency checkpoint format.
3. Maintenance-mode write fencing.
4. SQLite/local filesystem backup guidance.
5. PostgreSQL PITR / object-storage consistency guidance.
6. Restore validation CLI.
7. Ciphertext reconciliation.
8. Signing-key and trust-history validation.
9. Recovery epochs and audit continuity.
10. Recovery drill tooling and reporting.
