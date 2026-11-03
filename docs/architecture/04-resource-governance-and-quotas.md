# Resource Governance and Quotas

Sources: notes v1.9 (policy-based quotas) and v4.7 (hierarchical resource governance). **v4.7 is incomplete in the source notes**: it stops at §6 "Atomic reservation enforcement", and the rest is lost ([Q-GEN-2](../QUESTIONS.md)). Sections marked *(from v1.9)* come from v1.9. Sections marked **(planned 2026-11-03)** replace the lost v4.7 text and are now authoritative. Production values: [27-production-defaults.md](27-production-defaults.md). Resolved questions: Q-RES-*.

## Decision

Secure Intake enforces **hierarchical resource governance** at deployment, organisation and dropbox levels, with **durable reservations** and **fair background-job scheduling**. It stays self-hostable, works on SQLite or PostgreSQL, and needs **no Redis, message broker or external rate-limiting service** (database-backed coordination is enough initially).

> Every resource-consuming operation must be admitted against the applicable limits before it consumes capacity. Reserved capacity is accounted for atomically, and no organisation may monopolise shared infrastructure.

**Quotas are not billing.** The platform measures usage without subscriptions, invoices or payment processing.

Goals: predictable limits for standalone and multi-tenant deployments; no cross-tenant exhaustion; accurate accounting across API replicas; configurable without billing dependencies; reporting through API, CLI and web admin; safe behaviour at limits.

## 1. Hierarchy

```text
Deployment limits    total storage budget · global concurrency · worker capacity · request limits
Organisation limits  storage · submissions · upload sessions · background-job share
Dropbox limits       file size · submission size · file count · active uploads · rate limits
        ↓
Admission and reservation service   atomic checks · durable reservations · expiry · reconciliation
        ↓
Uploads · Storage · Workers
```

- The deployment limit is an **aggregate capacity constraint**. Organisation and dropbox limits are additional constraints, not allocations that must sum to the total (100 organisations may each have 100 GB while the deployment budget is 2 TB; admission enforces real aggregate capacity).
- Submission-level constraints (file count, file size, total bytes, upload session) apply within dropbox limits.

### Resolution

Typed quota resolution — not arbitrary JSON merging:

```text
Effective admission =
      Deployment capacity available
  AND Organisation allowance available
  AND Dropbox allowance available
  AND Operation-specific constraints satisfied
```

Unlike security policies ([23](23-security-policy.md)), quota overrides are **not** strengthening-only:

- An authorised operator may **raise** an organisation's quota within the deployment's resource policy (platform-level authorisation required).
- An organisation administrator may **lower** dropbox limits but never exceed the organisation's delegated ceilings.

Illustrative configuration (not recommended production values):

```json
{
  "resourceGovernance": {
    "deployment": {
      "storageBudgetBytes": 2199023255552,
      "maxConcurrentUploads": 500,
      "maxConcurrentWorkers": 32
    },
    "organisationDefaults": {
      "storageQuotaBytes": 107374182400,
      "maxConcurrentUploads": 20,
      "maxFileBytes": 1073741824
    },
    "dropboxDefaults": {
      "maxConcurrentUploads": 5,
      "maxFilesPerSubmission": 25,
      "maxSubmissionBytes": 5368709120
    }
  }
}
```

Byte limits are 64-bit integers; accounting periods use UTC; the API requires explicit units. Time-window quotas specify period boundary and calendar vs rolling reset. Quota policies are versioned and every change is audited.

## 2. Quota categories

| Category | Controls |
| --- | --- |
| Storage | Committed ciphertext, reserved bytes, temporary uploads, retained object count |
| Submission | Max files, ciphertext bytes, submission count (e.g. per day) |
| Upload | Concurrent sessions, chunk size, upload duration |
| API | Request rate, burst capacity, expensive operations |
| Workers | Concurrent jobs, weighted scheduling, queue backlog |
| Evidence | Concurrent exports, export size, temporary package storage |
| Dropboxes *(from v1.9)* | Active dropbox count |
| Bandwidth *(from v1.9)* | Ingress/egress bytes per period |
| Webhooks *(from v1.9)* | Events delivered per period (soft/backpressure) |

**Hard quotas** block operations that would exceed the limit; **soft thresholds** alert only.

Retention and legal holds are separate policies. **A quota never authorises deletion of legally retained evidence.**

## 3. Enforcement behaviour

| Condition | Behaviour |
| --- | --- |
| File exceeds max size | Reject before upload |
| Submission exceeds permitted total | Reject submission |
| Storage quota exhausted | Reject new reservation |
| Concurrent upload limit reached | Retryable admission failure (temporary throttling) |
| API rate limit exceeded | HTTP 429 |
| Worker concurrency reached | Queue eligible job |
| Evidence export concurrency reached | Queue or reject per operation policy |
| Monthly bandwidth exhausted | Reject or throttle new transfers per policy |
| Active dropbox limit reached | Reject creation/activation per policy |
| Storage crosses warning threshold | Alert only |
| Storage crosses critical threshold | Alert; optionally restrict new intake |
| Existing usage exceeds a reduced quota | Preserve files; restrict new consumption |

- Reducing a quota never triggers automatic deletion; exceeding one never bypasses retention, legal holds or deletion approvals.
- **Availability rule:** an organisation over quota can still perform authorised administration, security controls, evidence review, audit export, retention checks, deletion requests and incident resolution. Reserve a separate operational capacity budget for essential administration.

## 4. Storage accounting

Under E2EE the server can't verify plaintext size; client-declared plaintext length is untrusted metadata. **Enforcement uses verifiable ciphertext lengths only.**

| Quantity | Meaning |
| --- | --- |
| Committed | Authoritative retained ciphertext bytes (finalised) |
| Reserved | Capacity promised to in-progress operations |
| Temporary | Uploaded ciphertext not yet finalised |
| Reclaimable | Eligible for cleanup but not yet physically removed — **still counts** against physical capacity until deletion is confirmed |

Finer-grained tracked counters *(from v1.9)*: `logical_plaintext_bytes` (untrusted, reporting only), `ciphertext_payload_bytes`, `metadata_bytes` (chunks, manifests, recipient envelopes), `retained_object_bytes`, `pending_upload_bytes`, `superseded_file_bytes`, `deletion_pending_bytes`.

Logical quota accounting ≠ provider billing. Versioning, replication, multipart remnants and backups consume storage not reflected in logical totals. Report known logical usage and observed physical usage separately; never claim exact provider billing without authoritative provider data.

## 5. Reservations

Every upload session obtains a **durable reservation before receiving upload capabilities**. Example: 10 GiB remaining, two 8 GiB uploads — the first reserves 8 GiB; the second is rejected or offered a smaller allocation.

```text
Requested → Reserved → Uploading → Finalising → Committed
Exceptional: Expired · Cancelled · Released · Reconciliation required
```

- Transferred bytes are tracked and capped at the reserved maximum; on finalise, stored ciphertext is verified and converted to committed usage; unused capacity is released.
- A reservation is **never released just because the browser disconnected or the session expired**. Account for issued presigned URLs, outstanding multipart uploads and possibly-uploaded ciphertext; storage cleanup must confirm no objects remain first.

```csharp
public sealed record StorageReservation(
    Guid ReservationId, Guid OrganisationId, Guid DropboxId, Guid UploadSessionId,
    long ReservedBytes, DateTimeOffset ExpiresAt, string State,
    long ResourceGeneration);
```

Reservations bind to the tenant's current resource assignment generation ([03](03-tenancy-and-isolation.md)). A stale reservation can't authorise writes to a migrated or suspended tenant.

### Atomic enforcement **(planned 2026-11-03)**

> Source notes end at v4.7 §6. Everything from here on is planned.

Every admitted operation reserves against **all three scopes in one database transaction**, or reserves nothing.

**Counters.** There's one row per (scope, resource) in `quota_usage_counters(scope_type, scope_id, resource, committed, reserved, temporary, reclaimable, limit_value, version)`. `limit_value` is denormalised from the effective policy and refreshed whenever the policy changes.

**PostgreSQL:**

```sql
-- Fixed lock order: deployment → organisation → dropbox (prevents deadlocks)
UPDATE quota_usage_counters
   SET reserved = reserved + @n, version = version + 1
 WHERE scope_type = @scope AND scope_id = @id AND resource = 'storage_bytes'
   AND committed + reserved + temporary + reclaimable + @n <= limit_value
RETURNING version;
-- 0 rows → roll back the whole transaction → quota problem type for that scope
```

- All three updates, the `quota_reservations` insert and the `quota_usage_events` insert commit together.
- The deployment row is a write hotspot. If contention shows up in load tests, it's split into N stripes (`scope_id = deployment#k`), and admission picks a stripe with headroom. Not built until measured.
- Retries on serialisation failure are bounded (3) and then return a retryable `503` admission error, never a quota error.

**SQLite:** `BEGIN IMMEDIATE`, the same statements, and one writer. Transactions stay short, with no I/O to storage inside them.

**Invariant:** `committed + reserved + temporary + reclaimable ≤ limit_value` at every scope, at commit time. Reconciliation (§17) tests it continuously.

**Lifecycle hooks:**

| Event | Counter change |
| --- | --- |
| Session created | `reserved += requested` (bounded by grant, dropbox and organisation limits) |
| Chunk confirmed | `reserved -= len`, `temporary += len` (still within the original reservation) |
| Finalised | `temporary -= fileBytes`, `committed += fileBytes`, and any unused reservation released |
| Expired or cancelled | Reservation becomes `reclaimable` and is released only when cleanup confirms objects are gone ([13](13-resumable-uploads.md) §11) |
| Content deleted | `committed -= n`, `reclaimable += n`, then `reclaimable -= n` once storage deletion is verified |

## 6. Upload concurrency *(from v1.9)*

| Limit | Purpose |
| --- | --- |
| Active upload sessions | Prevent excessive reserved capacity |
| Active file transfers | Control simultaneous storage operations |
| Active uploads per dropbox | Stop one public dropbox monopolising capacity |

Enforced with short-lived, heartbeated **leases** in shared (database) state — in-memory counters are insufficient in distributed deployments. A crashed instance must not hold a slot permanently; lease expiry is reconciled against session and storage state before capacity is reused.

```text
upload_concurrency_leases(lease_id, organisation_id, dropbox_id, upload_session_id, expires_at, heartbeat_at, state)
```

## 7. Bandwidth *(from v1.9)*

- **Ingress:** encrypted uploads into platform- or organisation-managed storage.
- **Egress:** ciphertext retrieved by authorised organisation clients (reviewers and automation still download).
- **Internal transfer:** migrations, replication, operational transfers — accounted separately from user-facing quotas.

Direct-to-S3/R2 uploads bypass the API, so the storage adapter reconciles transfer receipts, object sizes and provider metrics. Provider metrics may be delayed or incomplete, so bandwidth usage carries an explicit **accuracy classification**. For BYO storage the organisation's provider is the authority on actual charges.

## 8. API rate limits *(from v1.9)*

Rate limits (bursts per minute) are distinct from quotas (totals per period). Dimensions: deployment, organisation, API client, authenticated human, public dropbox, source IP/network where appropriate. Public endpoints (email verification, PIN attempts, one-time code redemption, upload-session creation) get stricter limits.

Throttling returns standard rate-limit headers and `Retry-After`:

```http
HTTP/1.1 429 Too Many Requests
Retry-After: 30
Content-Type: application/problem+json

{ "type": "https://docs.example.com/problems/rate-limited", "title": "Rate limit exceeded",
  "status": 429, "detail": "Retry after the indicated interval." }
```

Resource-quota exhaustion uses a **distinct problem type** so clients can tell it apart from temporary throttling.

## 9. Fair background scheduling

Organisations get a weighted share of worker capacity. Hitting worker concurrency limits queues jobs rather than rejecting them.

### Design **(planned 2026-11-03)**

**Priority classes.** Each class is strictly ordered for *reserved* slots and weighted for shared slots.

| Class | Examples | Reserved slots |
| --- | --- | --- |
| `essential` | Receipt signing, security alerts, revocation fan-out, draft cleanup, audit checkpointing | 2 per worker process (never used by other classes) |
| `interactive` | Email, webhooks, invitation delivery | none (weight 3) |
| `bulk` | Evidence export and import, reconciliation, usage rollups, rewrap batches | none (weight 1) |

**Fairness across organisations.** The scheduler uses **deficit round robin** over organisations with eligible jobs, within each class.

1. Each organisation has `weight` (default 1, raised by an operator through the quota policy) and `maxConcurrentJobs` (default 4 per class).
2. To claim, a worker takes the eligible organisation with the lowest `running / weight` ratio, then that organisation's oldest due job:
   - PostgreSQL: `FOR UPDATE SKIP LOCKED LIMIT 1`.
   - SQLite: an in-process queue rebuilt from the table.
3. An organisation at `maxConcurrentJobs` is skipped, so one tenant's 10,000 exports can't starve another tenant's 3 emails.
4. Platform jobs (`organisation_id = null`) are scheduled as a pseudo-organisation with weight 2.

**Backlog limits.** Each organisation's queued `bulk` jobs are capped (default 1,000). Above the cap, new bulk requests get a quota problem response (`queue-full`). The `essential` and `interactive` classes are never rejected. They queue, and once age exceeds a threshold they raise `quota.worker.backlog_aged`.

**Visibility.** Queue depth, oldest-job age and running counts are reported per class and per organisation, using only the organisation IDs permitted by [21](21-observability-and-operations.md) §3.

## 10. Usage accounting *(from v1.9)*

Durable **usage ledger** plus materialised **counters** for fast checks, fed by an accounting service handling idempotent events, reservations and reconciliation.

- Record meaningful events (reservation created/adjusted, file committed, file deleted, transfer reconciled) — not every chunk.
- API request rates use bounded aggregation windows, not a per-request ledger.
- Every usage event has a stable idempotency key; retries must not double-count.

## 11. Alerts *(from v1.9)*

```text
quota.storage.warning | critical | exceeded
quota.bandwidth.warning | exceeded
quota.upload_concurrency.saturated
quota.api_rate.sustained_throttling
quota.reservation.leaked
quota.accounting.drift_detected
quota.storage_provider.capacity_warning
```

Configurable thresholds (e.g. warning 80%, critical 95%), deduplication and cooldowns. Exposed via admin UI, API, CLI, metrics and optional webhook forwarding ([05](05-notifications.md)).

## 12. Usage dashboard *(from v1.9)*

Organisation view: storage, monthly ingress/egress, active dropboxes — distinguishing current usage, reserved capacity and pending deletion, and noting that usage may lag provider reconciliation. Platform view aggregates organisations and flags unusual consumption.

## 13. Data model *(from v1.9)*

```text
quota_policies            quota_usage_events
quota_policy_assignments  quota_accounting_periods
quota_reservations        quota_reconciliation_runs
quota_usage_counters      quota_alert_rules
upload_concurrency_leases quota_alert_events
```

## 14. API *(from v1.9)*

```text
GET  /api/organisations/{id}/quotas
PUT  /api/organisations/{id}/quotas
GET  /api/organisations/{id}/usage
GET  /api/organisations/{id}/usage/history
GET  /api/organisations/{id}/usage/reservations
POST /api/organisations/{id}/usage/reconcile
GET  /api/organisations/{id}/quota-alerts
GET  /api/platform/quotas/defaults
PUT  /api/platform/quotas/defaults
```

## 15. Scope of categories (Q-RES-2) **(planned 2026-11-03)**

| Category | Status |
| --- | --- |
| Storage, submission, upload, API, workers, evidence | Enforced (v1.0, except evidence in v1.1 when `.siep` lands) |
| Active dropbox count | **Enforced in v1.0** (cheap, prevents sprawl) |
| Webhooks | Enforced as a **rate limit with backpressure** in v1.0. Deliveries are delayed, never dropped |
| Bandwidth | **Reporting only** from v1.2, with an accuracy classification. No enforcement until provider data is reliable enough |

## 16. Rate-limit implementation **(planned 2026-11-03)**

- Single process (standalone): in-memory token buckets.
- Multiple API replicas: **database-backed sliding-window counters** with 10-second buckets, in a `rate_limit_windows` table (unlogged on PostgreSQL), plus a small local pre-filter. These are approximate by design. Overshoot is bounded by `replicas × local burst`, which is acceptable for rate limits but not for quotas, so quotas always use §5.
- Order on public endpoints: IP limit → grant limit → only then DB lookups and crypto work ([12](12-upload-links-and-invitations.md) §13).
- Headers: `RateLimit-Policy` and `RateLimit` (IETF draft `httpapi-ratelimit-headers`), plus `Retry-After`.
- Problem types: `/problems/rate-limited` (temporary, retry), `/problems/quota-exceeded` (with `scope` and `resource`), `/problems/admission-busy` (retryable contention).

## 17. Reconciliation **(planned 2026-11-03)**

The **usage ledger is authoritative.** Counters are a cache.

| Job | Schedule | Action |
| --- | --- | --- |
| Counter vs ledger | Hourly per organisation (`bulk`) | Recompute from `quota_usage_events`. If drift is above 0, **correct the counter**, record a `quota_reconciliation_runs` row and raise `quota.accounting.drift_detected` |
| Ledger vs storage | Daily per storage assignment (`bulk`) | List objects (or use the provider inventory) and compare against committed manifests. Orphans older than the cleanup grace period are queued for deletion. Missing objects raise a **security finding**, never an automatic ledger change |
| Leaked reservations | Every 15 min (`essential`) | Expired reservations with no active session move to cleanup. Never released without storage confirmation (§5) |
| Lease reaper | Every minute | Expire leases past `expires_at` and reconcile against session state |

Reconciliation never deletes committed evidence, and never bypasses legal holds or retention.

## 18. Overrides **(planned 2026-11-03)**

- `quota_policy_overrides(scope_type, scope_id, resource, value, reason, expires_at?, created_by, approved_operation_id?)`.
- Platform raises to an organisation quota need `platform.quotas.manage` (Elevated). Raising above the deployment's `organisationMax` needs a Sensitive approval.
- Organisation lowering of a dropbox limit needs `org.dropboxes.manage`, can never exceed the organisation ceiling, and needs no approval.
- An override may be time-boxed. When it expires, the value reverts and existing usage over the limit follows §3 (preserve, restrict new consumption).
- Every policy or override change writes a new policy version and an audit event, and re-denormalises `limit_value` in the same transaction.

## 19. Usage reporting **(planned 2026-11-03)**

- `GET /api/organisations/{id}/usage` returns current counters with `committed`, `reserved`, `temporary`, `reclaimable`, `limit` and `asOf`.
- `…/usage/history` returns daily rollups (`quota_accounting_periods`) for 13 months.
- **Export** in CSV and JSON with stable column definitions. This is the integration point for anyone who wants to bill on usage (an operator or an integrator such as Swoovr). Secure Intake itself never bills (Q-ONB-6).
- CLI: `secureintake usage show|export --organisation …`.

## 20. Audit events **(planned 2026-11-03)**

```text
quota.policy.created | updated | override_created | override_expired
quota.reservation.rejected            (scope, resource, requested, available; sampled per grant)
quota.reconciliation.completed | drift_corrected
quota.backlog.rejected
quota.threshold.crossed               (warning | critical | exceeded)
```

Accepted reservations aren't audited one by one. They're in the usage ledger.

## 21. Implementation order **(revised 2026-11-03)**

| Release | Items |
| --- | --- |
| v1.0 | Policy registry and resolution · atomic storage reservations · upload concurrency leases · file and submission size limits · active dropbox count · API rate limiting · idempotent usage events · counter/ledger reconciliation · leaked-reservation recovery · fair scheduler with priority classes · basic alerts and usage view |
| v1.1 | Ledger vs storage reconciliation · overrides UI · usage export · evidence export quotas |
| v1.2 | Bandwidth reporting · deployment counter striping (only if measured) · historical reporting UI |
