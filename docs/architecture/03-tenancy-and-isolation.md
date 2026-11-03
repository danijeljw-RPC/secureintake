# Tenancy, Isolation and Tenant Migration

Sources: notes v2.0 (hybrid tenant isolation) and v4.6 (operator-managed tenant migration). v4.6 supersedes v2.0's in-platform migration engine. Resolved questions: [QUESTIONS.md](../QUESTIONS.md) (Q-TEN-*). Planning decisions applied below take precedence over the source notes.

> **Planning decisions applied (2026-11-03)** — these override the text below where they differ. See [QUESTIONS.md](../QUESTIONS.md).
> - v1.0 ships the **shared tier only**. Dedicated DB and storage tiers and migration cutover are v1.2. The routing and generation abstractions are still built in M1.
> - Naming: one **generation** · `organisation_resource_assignments` · `organisations` (Q-TEN-2). Dedicated DBs are operator-provisioned (Q-TEN-3). No BYO database, and BYO storage is v1.2 (Q-TEN-4).
> - Read-only reviewer access is allowed while the source is frozen and blocked during cutover (Q-TEN-6). SQLite → PostgreSQL utility in v1.1 (Q-TEN-7).

## Decisions

1. **Shared multi-tenant infrastructure by default**, with optional **dedicated database and/or storage** per organisation. Implemented as a **shared control plane** with independently assignable **tenant data planes**.
2. **Secure Intake does not physically migrate organisations** between databases or storage providers. Operators copy the data with their own tooling; Secure Intake provides planning, maintenance fencing, integrity manifests, validation, reconciliation, cutover controls and audit.

> Secure Intake controls whether a tenant can safely move and when its new resource assignments become authoritative. The operator controls how the data is physically copied.

## 1. Isolation tiers

Database and storage isolation are independently selectable. These are **deployment configurations, not commercial tiers**.

| Tier | Database | Storage | Typical use |
| --- | --- | --- | --- |
| Shared | Shared PostgreSQL | Shared bucket | Standard organisations |
| Dedicated storage | Shared PostgreSQL | Dedicated bucket/account | Storage separation required |
| Dedicated database | Dedicated PostgreSQL | Shared bucket | Database isolation required |
| Fully dedicated | Dedicated PostgreSQL | Dedicated bucket/account | Higher-isolation organisations |
| Standalone | SQLite | Local filesystem or S3-compatible | Single-node installations |

Dedicated resources give stronger infrastructure separation but **not** complete security isolation if the same processes and admin credentials reach every tenant. RBAC and cryptographic separation are still required.

## 2. Control plane vs data plane

The control plane knows where each organisation's data lives but never stores encrypted file contents.

**Control plane:** organisation identity and lifecycle; tenant routing assignments; deployment security policy; platform administrators and governance; infrastructure health and migration orchestration; references to DB/storage credentials; platform audit and operations.

**Tenant data plane:** dropboxes and access policies; submissions and revisions; encrypted file metadata and envelopes; organisation retention and legal holds; members' resource permissions; organisation audit events; usage counters and quota reservations.

Cross-boundary references use **stable IDs**, never cross-database foreign keys.

## 3. Database isolation

### Shared PostgreSQL

Every tenant-owned table carries an explicit `organisation_id` (simplifies auditing, partitioning, migration, isolation testing). Row-level security as defence in depth:

```sql
ALTER TABLE submissions ENABLE ROW LEVEL SECURITY;
ALTER TABLE submissions FORCE ROW LEVEL SECURITY;

CREATE POLICY submissions_tenant_isolation ON submissions
USING      (organisation_id = NULLIF(current_setting('app.organisation_id', true), '')::uuid)
WITH CHECK (organisation_id = NULLIF(current_setting('app.organisation_id', true), '')::uuid);
```

- Tenant context set **transaction-locally** from a trusted, validated organisation ID.
- Runtime role must not own tables or hold `BYPASSRLS`.
- Connection pooling must not leak tenant context between requests.
- RLS supplements application authorisation; it doesn't replace it.

### Dedicated PostgreSQL

Uses the **same logical tenant schema and migrations** as the shared database (keeps `organisation_id` for compatibility and validation). No separate entity models. Moving tiers must not require rewriting queries.

### Standalone SQLite

Control-plane and tenant schemas may share one database file while remaining logically separated in the application.

## 4. Tenant routing

Routing is resolved **server-side only**. Clients can never choose a database or storage profile via connection string, profile ID or header.

```csharp
public interface ITenantDatabaseResolver
{
    ValueTask<TenantDatabaseAssignment> ResolveAsync(Guid organisationId, CancellationToken ct = default);
}

public sealed record TenantDatabaseAssignment(
    Guid OrganisationId, string DatabaseClusterId, string DatabaseName, long AssignmentVersion);
```

- The authoritative assignment lives in the control plane (`organisation_resource_assignments`, see [02](02-organisation-onboarding-and-lifecycle.md)); cluster configuration and secret references live in a separate registry (`database_clusters`).
- Connections come from a **bounded** connection-factory pool — never one unbounded pool per dedicated database.
- Short-lived routing caches are allowed with explicit invalidation. Every assignment carries a monotonic **generation**; API instances and workers reject stale generations during cutover.

> Main constraint: no application service may assume all organisations share one database connection. Build shared tenancy first, but with hybrid-routing data-access abstractions from day one.

## 5. Storage isolation

| Storage mode | Isolation |
| --- | --- |
| Shared bucket | Tenant-specific object namespace with scoped application access |
| Dedicated bucket | Separate bucket with tenant-specific credentials |
| Dedicated account | Separate cloud account/project and credentials |
| BYO storage | Organisation-owned provider credentials and policies |
| Local filesystem | Dedicated directory or volume |

- A prefix is not a security boundary; credentials and access policies must enforce scope.
- Direct uploads use credentials/presigned operations scoped to one upload session and object key.
- Opaque keys: `tenants/{organisation-id}/objects/{random-object-id}` — a convention, not authorisation.
- The API resolves object keys from its own database. Never accept an arbitrary object key from a public request.

## 6. Background workers

Every tenant job carries routing context:

```json
{ "jobId": "job_123", "organisationId": "org_456", "type": "retention.evaluate",
  "routingVersion": 12, "payloadVersion": 1 }
```

- Workers resolve the current assignment before executing; stale generation → refresh or safely reschedule.
- Migrations must account for leased jobs, retries and outbox events.
- Separate logical queues/scheduling partitions per tenant where required, without mandating separate brokers. Dedicated worker processes for high-isolation tenants are an optional deployment feature.

## 7. Cross-database consistency

No distributed transactions. Instead: local transactions per database, durable outbox events, idempotent operations, explicit workflow state machines for cross-boundary operations, and reconciliation of partial failures. Example: an organisation stays `provisioning` until both control-plane state and tenant database preparation are confirmed.

## 8. Audit isolation

- Tenant audit events stay in the tenant data plane; platform administrative and routing events in the control plane.
- Dedicated tenants may store audit in their dedicated database with **signed checkpoints exported** to an independently controlled destination.
- This evidences continuity; it doesn't claim a DBA can't tamper with all local records.

## 9. Operational boundaries

- **Platform operational visibility:** DB availability, storage health, job failures, quotas, routing status, audit integrity — without tenant-sensitive content.
- **Tenant administration:** dropboxes, submission metadata, members, encryption keys, retention.
- **Cryptographic access:** possession of an authorised private key, independent of administrative access.

## 10. Backup responsibility by resource

Secure Intake doesn't run backups ([18-backup-and-recovery.md](18-backup-and-recovery.md)); responsibility is:

| Resource | Responsible |
| --- | --- |
| Control-plane DB, shared tenant DB, dedicated platform-managed DB | Platform operator |
| BYO tenant database (if ever supported) | Assigned by contract/configuration |
| Shared and dedicated platform-managed storage | Platform operator |
| BYO storage | Organisation, unless separately arranged |
| Organisation private keys | Organisation |

First release supports operator-provisioned dedicated databases only; organisation-supplied database credentials are deferred. A dedicated DB backup should restore that tenant alone. Shared-DB tenant restore requires controlled extraction and reconciliation, never restoring the whole database over production.

## 11. Complexity of the hybrid model

| Area | Shared only | Hybrid |
| --- | --- | --- |
| DB connections | One pool | Multiple bounded pools |
| Schema migrations | One deployment | Version tracking per database |
| Backups | Shared | Shared + tenant-specific |
| Routing | Simple | Authoritative registry |
| Workers | Shared | Routing-aware, optionally isolated |
| Tenant migration | N/A | Fenced, verified cutover controls |
| Monitoring | Aggregate | Aggregate + per-database health |
| Incident response | Shared blast radius | Granular isolation options |

## 12. Suggested database layout

```text
Control Plane PostgreSQL
├── organisations (platform registry)       ├── platform_operations
├── platform_identities                     ├── platform_audit_events
├── platform_roles                          ├── organisation_resource_assignments
├── database_clusters                       └── tenant_migration_* (see §13)
├── storage_resource_registry

Shared Tenant PostgreSQL                    Dedicated Tenant PostgreSQL
├── dropboxes          ├── retention_policies   └── same tenant schema
├── submissions        ├── legal_holds
├── submission_revisions ├── tenant_audit_events
├── encrypted_files    ├── usage_counters
├── file_key_envelopes └── durable_jobs
```

---

## 13. Tenant migration (operator-managed)

### Architecture

```text
Existing organisation (authoritative DB, storage profile, audit state)
  → Migration plan (target validation, approval, resource inventory, immutable migration ID)
  → Maintenance fence + consistent checkpoint (stop writes, drain jobs, expire upload capabilities, record audit position)
  → Database copy [operator tooling]   +   Ciphertext copy [operator tooling]
  → Validation and reconciliation (records, object digests, receipts, audit, keys, jobs)
  → Approved authoritative cutover (new assignment generation, old source fenced, verification report)
```

No source data is deleted in the cutover. Source cleanup is a separate approved operation after the rollback window and retention checks.

### Migration classes

| Migration | Example | Complexity |
| --- | --- | --- |
| Database only | Shared → dedicated PostgreSQL | Medium |
| Storage only | Shared S3 → dedicated R2 | Medium |
| Database + storage | Shared → dedicated infrastructure | High |
| Dedicated → shared | Dedicated → shared PostgreSQL | High |
| Local → distributed | SQLite/local FS → PostgreSQL/S3 | High (needs logical transformation utility) |
| Provider relocation | S3-compatible A → B | Medium |

Classes, not promises of universal compatibility. **One organisation per migration.** Batch moves are operator scripts invoking the tooling sequentially.

### State machine

```text
Draft → Validated → Approved → Fencing → Fenced → Checkpointed → External Copy
→ Reconciling → Ready for Cutover → Cutover → Post-Cutover Validation → Completed
```

Exceptional: `validation_failed`, `copy_failed`, `reconciliation_failed`, `cutover_failed`, `rollback_required`, `rolled_back`, `abandoned`, `source_cleanup_pending`.

An operator marking a step complete is never proof; the destination must pass independent validation.

### Immutable plan

```json
{
  "migrationId": "mig_01J...",
  "organisationId": "org_123",
  "source": { "databaseProfile": "shared-postgres", "storageProfile": "primary-s3", "assignmentGeneration": 7 },
  "destination": { "databaseProfile": "dedicated-postgres-01", "storageProfile": "dedicated-r2-01" },
  "scope": "database_and_storage",
  "mode": "offline_fenced",
  "requiresApproval": true
}
```

Also records expected schema versions, source/destination resource identities, storage namespace mapping and retention constraints. Any material change creates a new plan version and invalidates approvals bound to the old one.

### Maintenance fencing

Disabling the organisation in the UI is insufficient; all components must stop authoritative writes:

1. Reject new upload sessions. 2. Reject finalisation. 3. Stop revisions. 4. Stop workflow transitions. 5. Stop new evidence export/import. 6. Drain/suspend tenant jobs. 7. Block tenant configuration writes. 8. Block privileged tenant-modifying operations. 9. Account for outstanding presigned upload capabilities. 10. Establish a consistent source checkpoint.

Read-only access may remain if policy permits and the source is stable.

**Presigned URLs** stay valid until expiry; the fence needs a capability-drain period or storage-side revocation. Ciphertext uploaded after the fence but before expiry must be identified and reconciled.

**Fencing token** — monotonic tenant resource generation:

```csharp
public sealed record TenantResourceFence(
    Guid OrganisationId, long Generation, Guid MigrationId, DateTimeOffset ActivatedAt, bool WritesBlocked);
```

- Every authoritative tenant write validates the current generation; a worker with an old generation can't commit.
- Shared PostgreSQL: transactionally enforced check in the authoritative DB (a cached pre-check is not enough).
- Dedicated DB: control-plane assignment and tenant-side fence coordinated via a durable protocol.
- Control plane unavailable → protected writes **fail closed**.

### Database reconciliation

Secure Intake provides: source inventory; tenant-scoped logical export validation; schema compatibility checks; destination identity verification; record-count and digest reconciliation; isolation validation; cutover readiness.

The plan defines which control-plane references are updated vs which tenant records are copied (control-plane records aren't blindly duplicated). Referential checks: submission→dropbox; form/workflow versions; file/ciphertext objects; recipient envelopes; receipt and evidence manifests; retention/legal holds; jobs and outbox.

Authenticated checkpoint manifest (counts alone are insufficient — use canonical deterministic representations with stable ordering and explicit exclusions for destination-specific fields; avoid exposing personal data):

```json
{
  "migrationId": "mig_01J...", "organisationId": "org_123", "sourceGeneration": 7,
  "checkpointId": "chk_456", "schemaVersion": 31,
  "recordInventories": {
    "submissions": { "count": 12500, "digest": "sha256:..." },
    "files": { "count": 38600, "digest": "sha256:..." }
  },
  "objectInventoryDigest": "sha256:...", "auditCheckpointId": "audit_789"
}
```

### Ciphertext migration and verification

- Preferred: **identity-preserving copy** (bytes, envelopes and manifests unchanged).
- Otherwise: explicit **object-reference remapping**; digest must be unchanged; receipts never rewritten.

```csharp
public sealed record MigratedObjectMapping(
    Guid FileId, string SourceStorageProfile, string SourceObjectKey,
    string DestinationStorageProfile, string DestinationObjectKey,
    string ExpectedSha256, long ExpectedLength);
```

Per object: exists; length matches; digest matches; version/immutable identity recorded; required metadata present; access policy correct; no unexpected public access. **S3 ETags are not SHA-256 proofs.** Verification streams in bounded memory.

| Level | Behaviour | Cutover suitability |
| --- | --- | --- |
| Metadata | Existence, length, provider metadata | Insufficient alone |
| Sampled | Digest on a sample | Diagnostic only |
| Full | Digest of all referenced objects | **Required by default** |
| Full + policy | Digests + access, retention, namespace checks | Recommended |

Object classification: referenced+verified; referenced but missing; referenced but digest-mismatched; unreferenced temporary upload; unreferenced orphan; retained by legal hold; unknown. Missing or mismatched referenced objects **block cutover**. Orphans are reconciled later, never auto-deleted during cutover.

### Receipts, evidence and audit continuity

Verify receipt signatures and issuer trust, evidence manifest digests, ciphertext digests, recipient envelope snapshots, original acceptance timestamps and evidence package references. Originals are never changed.

A **migration attestation** (`migration_id`, `organisation_id`, source/destination assignment, source checkpoint digest, destination reconciliation digest, cutover time, approvals, `signing_key_id`) is added alongside — it never replaces receipts, and only proves completeness to the extent reconciliation evidence supports.

Audit: preserve sequence, hash-chain links, signed checkpoints, signing-key trust history, external checkpoint references. Canonical contents and ordering are never rewritten.

```text
Source audit checkpoint → Migration fence event → Migration attestation
→ Destination activation event → Continued audit chain
```

If the chain format can't continue across the boundary, start an **explicitly linked new audit epoch** rather than pretend continuity.

### Cutover

Prerequisites: source fenced; no active writers; presigned drain complete or accounted for; destination DB validated; ciphertext fully verified; receipt and audit verification passed; keys and trust registries available; jobs/outbox reconciled; approvals valid; rollback plan documented.

Compare-and-swap assignment change, in one transaction with migration and fence state:

```sql
UPDATE organisation_resource_assignments
SET database_profile_id = @destination_database,
    storage_profile_id  = @destination_storage,
    assignment_generation = assignment_generation + 1
WHERE organisation_id = @organisation_id
  AND assignment_generation = @expected_generation;
-- 0 rows → cutover fails (concurrent change)
```

The tenant stays in restricted maintenance until post-cutover validation completes.

### Rollback

| Stage | Permitted recovery |
| --- | --- |
| Before fencing | Cancel migration |
| Fenced, before copy | Release fence |
| Copying | Abandon destination copy; release fence after validation |
| Reconciled, before cutover | Cancel cutover; resume source |
| Cutover, before destination writes | Controlled assignment rollback |
| After destination writes | Forward recovery or new migration |

Once destination writes are accepted, going back is a new controlled migration — the UI and CLI must make this **rollback boundary** explicit. The source stays protected for an operator-defined rollback window and is never auto-deleted on success.

### Failure recovery

| Failure | Response |
| --- | --- |
| Source fence can't complete | Abort before checkpoint; investigate writers |
| DB copy incomplete | Keep source fenced or safely resume source |
| Missing destination objects | Recopy; re-verify |
| Ciphertext digest mismatch | Quarantine destination object; block cutover |
| Receipt verification failure | Investigate trust/evidence; block cutover |
| Destination credentials fail | Repair profile; rerun preflight |
| Cutover transaction fails | Keep fence; inspect authoritative assignment |
| Destination fails before writes | Controlled rollback |
| Destination fails after writes | Forward recovery or new migration |

Recovery actions are idempotent where possible. Never reactivate the source without checking the authoritative generation and writer fences.

### CLI

```bash
secureintake tenant-migration plan --organisation <id> --database-profile dedicated-postgres-01 --storage-profile dedicated-r2-01
secureintake tenant-migration preflight   --migration <id>
secureintake tenant-migration fence       --migration <id>
secureintake tenant-migration checkpoint  --migration <id>
# operator copies database and objects externally
secureintake tenant-migration verify      --migration <id> --full-objects
secureintake tenant-migration cutover-check --migration <id>
secureintake tenant-migration cutover     --migration <id>
secureintake tenant-migration postflight  --migration <id>
```

Same JIT/approval controls as the API ([22](22-access-control.md)). No flag bypasses missing ciphertext, invalid receipts or isolation failures.

### Admin UI

Desktop-only page showing source/destination, per-check status (fence, DB copy, object verification, receipt verification, audit continuity, cutover approval), the exact failed checks and required operator actions. **No "force migration" button.**

### Data model

```text
tenant_migration_operations           tenant_migration_object_mappings
tenant_migration_plan_versions        tenant_migration_verification_runs
tenant_migration_approvals            tenant_migration_verification_findings
tenant_migration_fences               tenant_migration_cutover_records
tenant_migration_checkpoints          tenant_migration_attestations
tenant_migration_record_inventories   tenant_migration_rollback_records
                                      tenant_migration_source_cleanup_records
```

The control-plane resource assignment stays authoritative; migration records are never an alternative routing source.

### Audit events

```text
tenant_migration.planned | approved | fence_started | fence_completed | checkpoint_created
tenant_migration.external_copy_declared | verification_started | verification_completed | verification_failed
tenant_migration.cutover_requested | cutover_completed | cutover_failed
tenant_migration.rollback_requested | rollback_completed
tenant_migration.source_cleanup_requested | source_cleanup_completed
```

Include migration ID, organisation ID, generations, manifest digests, verification reports, approvals, outcome. Never credentials, private keys or decrypted data.

## 14. Implementation order

1. Control-plane / tenant-data-plane boundaries and tenant-scoped repository interfaces.
2. Shared PostgreSQL with RLS and cross-tenant isolation tests.
3. Authoritative routing registry with generations.
4. Dedicated storage profiles and scoped credentials.
5. Dedicated PostgreSQL support (operator-provisioned).
6. Routing-aware workers and bounded connection pooling.
7. Migration plan and compatibility validation.
8. Maintenance fencing with monotonic generations.
9. Checkpoint, record inventory and destination DB reconciliation.
10. Full ciphertext verification; receipt/evidence/audit continuity checks.
11. Approval-controlled cutover, post-cutover validation and rollback.
12. Source cleanup governance; CLI, admin UI and audit.
13. Optional dedicated worker deployments.
