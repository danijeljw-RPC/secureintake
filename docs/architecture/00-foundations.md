# Foundations

Status: **planning decision, 2026-11-03.** Replaces architecture versions v1.0 to v1.7, which were lost ([Q-GEN-1](../QUESTIONS.md)). Everything here was decided fresh, to be consistent with what v1.8 to v4.7 assume. Where a later doc relied on a foundation that no longer exists, this doc is now the authority.

Secure Intake is a **clean-room build**. No code, tests or assets come from Sprag or any other GPL project ([Q-GEN-3](../QUESTIONS.md)).

---

## F1. Product boundary

- Secure Intake is a standalone product. **Swoovr is an external API consumer**, like any other integrator ([Q-GEN-5](../QUESTIONS.md)). There's no Swoovr-specific code, configuration or data model. Swoovr's needs are met through generic features: API clients (service accounts), `api_issued` upload grants, the `externalReference` field and signed webhooks.
- Inbound-only, server-blind E2EE, and the operator owns the infrastructure. See [01-overview.md](01-overview.md) and the principles in [DECISIONS.md](../DECISIONS.md).

## F2. Naming

| Thing | Name |
| --- | --- |
| Product (working name) | **Secure Intake** |
| Server executable and OCI image | `secureintake` |
| Reviewer, automation and verifier client | `secureintake-client` (distributed separately) |
| .NET namespaces and projects | `SecureIntake.*` |
| Env var prefix | `SECUREINTAKE__Section__Key` |
| File formats | `.sif` encrypted file · `.sik` key package · `.sir.json` receipt · `.siep` evidence package |
| Metrics prefix | `secureintake_` |
| Protocol identifiers | `secure-intake-*` format names · `/.well-known/secure-intake/…` · DNS TXT `_secure-intake` |

The product name is generic and hasn't been checked for trademarks. That check is due before any public release (see [REVIEW.md](../REVIEW.md)).

## F3. Technology stack

| Layer | Choice |
| --- | --- |
| Server | .NET 10 (LTS), ASP.NET Core minimal APIs, **Native AOT**. Source-generated JSON, no reflection-based serialisation |
| Data access | Hand-written SQL over Npgsql and Microsoft.Data.Sqlite. Small, AOT-safe micro-mapping (Dapper AOT or source-generated). **No EF Core** (AOT friction and query opacity in security-critical paths) |
| Databases | PostgreSQL 16+ (shared or dedicated, with RLS) · SQLite 3.45+ (standalone) |
| Object storage | S3-compatible API (AWS S3, Cloudflare R2, MinIO, Ceph) · local filesystem (standalone) |
| Portal and admin UI | TypeScript, Vite, React, Tailwind CSS. The portal has a strict dependency budget: React, react-dom, the crypto libraries in [26](26-cryptographic-specifications.md), and nothing loaded at runtime from third parties |
| Reviewer client | .NET 10 AOT CLI first. Desktop GUI later on **Avalonia** |
| Crypto | See [26-cryptographic-specifications.md](26-cryptographic-specifications.md) |

## F4. Modular monolith

One deployable, with module boundaries enforced by project references and architecture tests (no module reaches into another's tables).

```text
SecureIntake.Host            composition root · serve --role all|api|worker · CLI verbs
SecureIntake.Api             HTTP endpoints · problem+json · versioning header
SecureIntake.Contracts       public DTOs · OpenAPI · shared with clients
SecureIntake.Domain          entities · state machines · invariants (no I/O)
SecureIntake.Application     use cases · authorisation evaluators · policies
SecureIntake.Infrastructure  DB · storage · email · secrets · signing providers
SecureIntake.Cryptography    protocol primitives · format codecs · test vectors (no server I/O)
SecureIntake.Worker          job runners · scheduler · outbox dispatcher
SecureIntake.Client.*        reviewer client (Core, Crypto, Auth, Storage, Cli, Desktop)
```

Modules: **Platform** (organisations, routing, deployment policy), **Identity** (principals, sessions, MFA, OIDC), **Access** (RBAC, JIT, approvals), **Intake** (dropboxes, grants, upload sessions, submissions), **Forms**, **Keys** (recipient keys, devices, key governance), **Review** (workflows, assignments, notes), **Evidence** (receipts, packages, import), **Notifications**, **Governance** (quotas, retention, legal holds), **Operations** (jobs, health, telemetry, backup fencing), **Audit**.

## F5. Identifiers

- Internal primary keys: **UUIDv7** (time-ordered).
- External IDs: a type prefix plus 26-character Crockford base32 of the UUIDv7, for example `org_01JD…`, `dbx_`, `grt_`, `upl_`, `sub_`, `file_`, `key_`, `usr_`, `rcpt_`, `job_`, `elev_`, `appr_`. The prefix is validated on input. Using an ID of the wrong type is a 404, never a coercion.
- Public submission reference (shown to uploaders): see F9.

## F6. Dropbox model

A **dropbox** is an organisation-owned intake destination.

| Field | Notes |
| --- | --- |
| `id`, `organisationId`, `name`, `description` | Description is server-readable presentation |
| `status` | `draft → active ⇄ paused → archived`. Only `active` accepts new upload sessions. `archived` is read-only and kept for retention |
| `formAssignment` | Pinned form-template version plus presentation overrides ([11](11-forms-and-templates.md)) |
| `recipientPolicyVersion` | [09](09-key-management-and-recipients.md). Required before activation |
| `workflowVersion` | [10](10-review-workflows.md). The default workflow if unset |
| `limits` | Within the organisation ceilings ([04](04-resource-governance-and-quotas.md)) |
| `retentionPolicyId` | F10. Inherits the organisation default |
| `externalReference` | Optional `{ system, id }` for integrators. Opaque to Secure Intake |

A dropbox has **no public URL of its own**. It's reached only through Upload Access Grants ([12](12-upload-links-and-invitations.md)). Activation requires: at least one active recipient key that can decrypt (individual or team-expanded), a recovery recipient if the organisation policy requires one (F12), a pinned form and a valid workflow.

## F7. Submissions and revisions (append-only)

```text
Submission (sub_…, public reference)
  └── Revision 1  (finalised upload session → immutable manifest → receipt)
  └── Revision 2  (created only via a "more information requested" grant)
  └── …
```

- A revision is immutable once finalised. It pins the form snapshot, recipient snapshot, file manifests, envelopes and evidence manifest.
- "More information requested" issues a new `invitation` grant scoped to `submissionId` (revision N+1). Nothing is ever sent back to the uploader except that link.
- Each revision gets its own receipt. Receipts never cover more than one revision.
- Reviewer-side data (notes, workflow state, assignments) attaches to the submission, not to revisions.

## F8. Uploader access modes and return access

- Identity modes: `anonymous`, `verified_email` (OTP), `one_time_code`, `oidc` ([12](12-upload-links-and-invitations.md), [14](14-uploader-identity.md)).
- **Return access** in v1 is `status_only`: with a return-access capability (delivered once at finalisation as a fragment link `/s/{reference}#sir_…`), the uploader can see the public status and download their receipt. Return access never exposes files, form values or reviewer data.

## F9. Public reference format

`SI-XXXXX-XXXXX`: a deployment-configurable prefix (default `SI`) followed by 10 Crockford base32 characters (9 random, 45 bits, plus 1 check character), shown in two groups of five. It's unique per deployment and enforced by a unique index, with retry on collision. A reference is a label, not a capability: knowing it grants nothing.

## F10. Retention and legal holds

- A **retention policy** is a versioned organisation object: `retainFor` (duration from finalisation), `onExpiry` (`delete` or `review`), and `minimum` (deployment floor). The organisation default applies to dropboxes that don't set one.
- A **legal hold** is placed by an Owner or Security Officer with JIT elevation, on a submission, a dropbox or the whole organisation. A hold blocks deletion, retention expiry, quota-driven cleanup and organisation offboarding deletion. Releasing a hold needs one independent approval.
- Deletion is a durable job: tombstone → delete objects → verify → reconcile quota → audit. Receipts and audit records outlive content deletion as tombstoned records.
- Defaults: [27-production-defaults.md](27-production-defaults.md).

## F11. Audit

- Two scopes: **platform audit** (control plane) and **organisation audit** (each tenant data plane).
- Append-only tables. Every record carries `seq`, `prevHash` and `hash = SHA-256(prevHash ‖ canonical(record))`. Canonicalisation is JCS (RFC 8785).
- A **signed checkpoint** (`seq`, `hash`, `recoveryEpoch`, time) is signed with the Audit Checkpoint key (ES256) every 1,000 records or 15 minutes, whichever comes first. Checkpoints are also written to an operator-configured external sink: a filesystem path, an S3 bucket with object lock, or syslog. Restores are detected against that sink ([18](18-backup-and-recovery.md)).
- The audit write is in the same transaction as the state change it records. If audit fails, the operation fails.
- Never logged: secrets, bearer tokens, OTPs, PINs, private keys, plaintext DEKs or E2EE plaintext.

## F12. Durable jobs and outbox

- **Outbox:** domain events are written in the business transaction (`outbox_events`), then dispatched by workers. Delivery is at least once. Payloads are versioned (`type@v`) and every consumer is idempotent.
- **Jobs:** `jobs(id, organisation_id, class, type, payload_version, payload, state, priority_class, attempts, not_before, lease_owner, lease_expires_at, idempotency_key, generation)`.
  - Claiming: PostgreSQL uses `FOR UPDATE SKIP LOCKED` under the fair scheduler in [04 §9](04-resource-governance-and-quotas.md). SQLite uses a single in-process dispatcher.
  - Leases are heartbeated, and an expired lease is re-claimable. Retries use exponential backoff with jitter. After `maxAttempts` a job moves to `dead`, raises an alert, and can only be replayed manually (an Elevated permission).
  - Jobs carry the tenant **generation**. A stale generation is rejected at execution (fencing, [03](03-tenancy-and-isolation.md)).
- No Redis and no broker, at any scale in v1.

## F13. Deployment profiles

| Profile | Purpose | Key differences |
| --- | --- | --- |
| **Development** | Local work | Self-signed TLS allowed; `internal_test` grants; verbose diagnostics; relaxed rate limits; banner on every page; refuses to start if `environment = production` |
| **Production** | Default for real use | All baseline controls; HTTPS required; secrets must not be in plain config files |
| **Hardened** | High-assurance tenants | Production plus: phishing-resistant MFA for all admins; desktop/CLI-only for Sensitive operations; Organisation Key Directory required ([26](26-cryptographic-specifications.md) §5); recovery recipient mandatory; stricter defaults ([27](27-production-defaults.md)); external audit checkpoint sink mandatory |

A profile is a security baseline. It's separate from the environment label (`dev`, `staging`, `production`).

## F14. Platform and organisation roles

The role catalogue in [22-access-control.md](22-access-control.md) is confirmed as complete for v1, with one addition: the platform service role **Provisioning Client** for API-driven organisation provisioning. Integrators such as Swoovr use the organisation **API Client** role, scoped by permission. Permissions: [28-permission-catalogue.md](28-permission-catalogue.md).

## F15. BYO storage

An organisation may supply its own S3-compatible bucket. The operator approves the binding, and the storage credentials are held through the secret-resolution system ([19](19-configuration-and-secrets.md)). This is **post-v1** (planned v1.2), but the storage adapter and the routing assignment model support it from day one. BYO **database** is not planned: dedicated databases are operator-provisioned ([03](03-tenancy-and-isolation.md)).

## F16. Architecture decision records

The v1.8 to v4.7 series is closed. New decisions are recorded as ADRs in `docs/adr/ADR-NNNN-title.md` (context, decision, consequences, status), with one line added to [DECISIONS.md](../DECISIONS.md).
