# Deployment, Packaging and Upgrades

Sources: notes v2.1 (operator-managed infrastructure) and v4.0 (native binaries and containers, operator-controlled upgrades). v4.0 wins on overlaps. Resolved questions: [QUESTIONS.md](../QUESTIONS.md) (Q-DEP-*). Planning decisions applied below take precedence over the source notes.

> **Planning decisions applied (2026-11-03)** — these override the text below where they differ. See [QUESTIONS.md](../QUESTIONS.md).
> - Health endpoints: `/health/live`, `/health/ready` and `/health/status` (Q-DEP-1). API version header: `SecureIntake-Api-Version` (Q-PRT-1).
> - Production servers run on Linux x64 and arm64 only. Windows x64 and macOS arm64 are for standalone and dev. macOS x64 is dropped. One LTS a year, supported 18 months (Q-DEP-2).
> - Signing: Sigstore keyless plus a hardware-held project release key. Registry: ghcr.io (Q-DEP-3).
> - Executable name `secureintake` (Q-OPS-1). Kebab-case `secure-intake` is kept only in protocol identifiers (format names, `/.well-known/secure-intake/…`, DNS TXT labels).

## Decisions

1. **Secure Intake never creates, purchases, resizes or manages cloud infrastructure.** The operator supplies database, storage, networking, certificates and supporting infrastructure. The application validates configuration, connects, and manages its own application lifecycle. Applies to standalone, containerised and distributed deployments.
2. **Ships as Native AOT executables and OCI images.** Operators control installation, upgrades, rollback and orchestration. Secure Intake provides release verification, migration tooling, compatibility checks and deployment health reporting, but **never auto-downloads or installs updates**.

## 1. Responsibility boundary

| Resource | Operator | Application |
| --- | --- | --- |
| Compute | Servers, containers, orchestration | Processes and health |
| Database | Provisioning, backups, credentials | Schema, queries, migrations |
| Object storage | Buckets, accounts, access policies | Encrypted objects and lifecycle |
| Networking | DNS, firewall, ingress, TLS | Routes, headers, security validation |
| Secrets | Secret stores and delivery | Loading, validation, redaction |
| Monitoring | Monitoring infrastructure | Metrics, traces, health endpoints |
| Deployment | Rollout/rollback execution | Version checks and readiness |
| Tenant isolation | Dedicated resources where required | Tenant routing and access enforcement |

The app may create/delete objects inside an authorised storage profile but never provisions buckets or accounts; it migrates its schema but never creates a database instance.

## 2. Deployment modes

| Mode | Shape | Fit |
| --- | --- | --- |
| Standalone | Single Native AOT executable; SQLite; local FS or object storage; in-process workers | Small installations |
| Container | OCI image with configurable API/worker roles; operator DB and storage | Standard hosting |
| Distributed | Multiple API replicas and workers; PostgreSQL; shared storage; authoritative tenant routing | Higher scale |

All modes run the same domain and application services. Standalone needs no Redis, RabbitMQ, Kubernetes or external IdP. The app remains usable without Kubernetes or cloud-specific tooling.

## 3. Executable and roles

One host executable with explicit roles (shared code, only required services activated):

```bash
secureintake serve --role all       # standalone
secureintake serve --role api       # distributed
secureintake serve --role worker    # distributed
```

The release also includes a separately runnable **offline verifier** (no production credentials or DB connectivity needed) — see [15](15-receipts.md), [16](16-evidence-packages.md).

## 4. Distribution pipeline

```text
Tagged source release (pinned deps · reproducible inputs · release manifest)
→ CI build and verification (Native AOT · unit/integration · protocol compatibility · security checks)
→ Native executables (Linux · Windows · macOS)  +  OCI images (API · Worker · Combined)
→ Signed release manifest and SBOM (digests · provenance · build metadata · dependency inventory)
→ Operator: verify → preflight → migrate → deploy → validate
```

All artifacts in a release advertise the same version and compatible protocol/schema ranges. Immutable artifacts only — never build on production servers.

## 5. Platform targets

| Platform | Arch | Artifact |
| --- | --- | --- |
| Linux | x64 | Native binary + OCI |
| Linux | ARM64 | Native binary + OCI |
| Windows | x64 | Native executable |
| Windows | ARM64 | Later, subject to CI validation |
| macOS | ARM64 | Native binary |
| macOS | x64 | Native binary |

The real support matrix is defined by passing automated AOT builds and tests, not by RID availability. **Linux is the recommended production platform**; Windows/macOS serve self-hosting, development and administration.

```bash
dotnet publish src/SecureIntake.Host -c Release -r linux-x64 \
  -p:PublishAot=true -p:SelfContained=true -p:StripSymbols=true -o ./artifacts/linux-x64
```

AOT cross-compilation isn't universal — build on target-compatible runners. **Trimming/AOT warnings are release-blocking** unless explicitly reviewed.

## 6. Release artifacts (proposed names)

```text
secureintake-4.0.0/
├── binaries/  secureintake-{linux-x64,linux-arm64,osx-arm64,osx-x64}.tar.gz, secureintake-win-x64.zip
├── containers/image-digests.json
├── deployment/compose.yaml, compose.production.example.yaml, kubernetes/
├── schemas/configuration.schema.json, release-manifest.schema.json
├── security/sbom.spdx.json, provenance.intoto.jsonl, release-manifest.sig
├── release-manifest.json
├── checksums.sha256
└── RELEASE-NOTES.md
```

Migrations and runbooks ship with the release (v2.1 also listed `migrations/`, `configuration/` examples).

## 7. OCI images

- Multi-arch (Linux x64/ARM64): `registry.example.com/secureintake:4.0.0`, `:4.0`, `@sha256:...`. **Pin by digest in production**; tags are never evidence of identity.
- Minimal runtime: AOT executable, required native libs, CA certificates, static web assets, version metadata. No compiler/SDK/shell unless justified.
- Non-root, read-only root filesystem where practical; explicit writable mounts (temp, local storage, operational state).

## 8. Versioning

SemVer for the application; separate compatibility versions for persistent formats and protocols:

| Version | Purpose |
| --- | --- |
| Application | Release compatibility, change management |
| Database schema | Persistent storage compatibility |
| Public API contract | Client compatibility (date-based, e.g. `2026-11`) |
| Encryption protocol | Ciphertext interoperability |
| Receipt format | Signed receipt verification |
| `.siep` format | Portable evidence package compatibility |
| Configuration schema | Operator configuration compatibility |
| Workflow/form schemas | Tenant configuration compatibility |

A new app release never implies a new encryption protocol or evidence format. **No mandatory `/v1` URL prefixes**; negotiate via headers, content types or capability metadata.

```json
{
  "applicationVersion": "4.0.0", "buildCommit": "abcdef123456",
  "databaseSchema": { "controlPlane": 21, "tenantData": 31 },
  "protocols": { "api": "2026-11", "encryption": [1], "receipt": [1], "evidencePackage": [1] },
  "configurationSchema": 4
}
```

## 9. Compatibility manifest

Machine-readable, generated from tested release metadata (values illustrative):

```json
{
  "release": "4.0.0",
  "database": { "minimumSupportedSchema": 29, "maximumSupportedSchema": 31, "targetSchema": 31 },
  "rollingUpgrade": { "supportedFrom": ["3.9.2"], "mixedVersionMinutes": 30 },
  "rollback": { "binaryRollbackSupportedTo": ["3.9.2"], "databaseRollbackSupported": false }
}
```

The app **refuses to start** against a schema outside its declared range. For hybrid tenancy, schema status is tracked per database assignment; an instance meeting an incompatible tenant schema fails that tenant's requests safely rather than migrating it.

## 10. Database migrations

Versioned, ordered, checksummed, idempotent where practical, migration-locked, **explicitly operator-executed**, tested on SQLite and PostgreSQL, audited. Built-in tooling (no external framework at runtime).

```bash
secureintake database status
secureintake database migrate --plan     # shows exactly what would run
secureintake database migrate --apply    # requires confirmation or explicit non-interactive flag
secureintake database verify
```

Production API/worker processes **never auto-migrate on startup**.

### Expand and contract

- **A — Expand:** add tables, nullable columns, compatible indexes.
- **B — Deploy:** roll out the new app, still compatible with the previous version.
- **C — Backfill:** resumable, bounded jobs.
- **D — Contract:** remove obsolete fields in a later release once the old version is unsupported.

Renaming/dropping a column the running version needs is not rolling-compatible. SQLite standalone uses maintenance-mode upgrades.

### Failure handling

Record `migration_id, migration_checksum, started_at, completed_at, status, application_release, operator_identity`. Use transactional DDL where supported; otherwise resumable steps with explicit recovery. A failed migration **blocks readiness** until reconciled.

## 11. Rolling upgrades (distributed PostgreSQL)

Only when the compatibility manifest permits. Default sequence (release manifest specifies the actual order; some releases need API-first or full maintenance):

1. Verify release (signatures, digests, compatibility, config)
2. Preflight (backup readiness, schema, storage health)
3. Expand schema
4. Upgrade workers (drain old, activate new)
5. Upgrade API nodes gradually behind the load balancer
6. Validate (health, receipts, uploads, queues, audit)
7. Complete (record activation, enable new capabilities)

### Worker compatibility

Job payloads are versioned. New workers handle supported older versions; old workers never claim payload versions they don't understand (compatibility predicate in the leasing query/dispatcher).

```json
{ "jobType": "receipt.sign", "payloadVersion": 2, "payload": { "submissionId": "sub_123", "receiptId": "rcpt_456" } }
```

Draining: stop claiming → finish active jobs within grace period → release/expire leases safely → start replacements → resume. Jobs stay idempotent and recoverable.

### API/client compatibility

Portal, reviewer clients and CLI update independently of the API. Capability document:

```text
GET /.well-known/secure-intake/capabilities
```

```json
{ "applicationVersion": "4.0.0", "apiContract": "2026-11", "supportedEncryptionProtocols": [1],
  "supportedReceiptFormats": [1], "supportedEvidencePackages": [1], "minimumReviewerClient": "3.8.0" }
```

Clients reject unsupported crypto combinations rather than downgrade. Portal static assets use content-hashed filenames and immutable caching; the HTML entry point and bootstrap config are revalidated.

## 12. Rollback

Rollback is a compatibility operation, not just swapping a binary.

| Type | Supported when |
| --- | --- |
| Binary | Previous binary supports current schema and protocol state |
| Container | Previous image supports current schema and persisted jobs |
| Configuration | Previous config still valid and secure |
| Database | Explicit migration path, or restored backup |
| Cryptographic protocol | Only if no unsupported ciphertext/protocol state was created |
| Full recovery | Operator restores a coordinated backup set ([18](18-backup-and-recovery.md)) |

Default: binary rollback within a tested compatibility window; otherwise forward-fix or backup recovery. Encryption envelope versions stay readable throughout the window — a decryption suite is never removed just because it's no longer allowed for new uploads.

### Irreversible operations

New encryption protocol, receipt signature profile, evidence format, persistent job payload, destructive migration, key retirement. Preflight flags them and the operator must **explicitly acknowledge**:

```text
Upgrade: 3.9.2 -> 4.0.0
[PASS] Configuration compatible
[PASS] Database migration plan valid
[PASS] Storage profiles compatible
[PASS] Signing keys available
[WARN] New job payload version introduced
[WARN] Binary rollback window limited
[PASS] No destructive schema migrations
Result: UPGRADE PERMITTED
```

## 13. Supply-chain integrity

- OCI image signatures; signed release manifest; checksums; build provenance attestations; published signing identity and rotation policy.
- Sigstore-compatible signing is acceptable **if** offline verification with pinned trust material is also supported. A signature only means something if the signer is trusted independently; a compromised download server must not be able to swap both artifact and key undetected.

```bash
secureintake release verify --manifest release-manifest.json --signature release-manifest.sig --trusted-key release-public-key.pem
secureintake release verify --artifact secureintake-linux-x64.tar.gz --manifest release-manifest.json \
  --signature release-manifest.sig --trusted-key release-public-key.pem
```

**SBOM:** SPDX JSON primary, CycloneDX optional. Covers .NET packages, native libs, crypto dependencies, container base, bundled frontend deps, relevant build tools — exact versions and relationships. Publish a security advisory process mapping advisories to releases so operators can check exposure without upgrading.

**Provenance:** `source_repository, source_commit, release_tag, build_workflow, build_runner_identity, toolchain_versions, dependency_lock_digest, artifact_digest, build_timestamp` — signed/attested. Reproducible builds are a goal to be demonstrated, not assumed (AOT varies across toolchains/linkers).

## 14. Health, readiness and verification

```text
GET /health/live     # process alive; no external dependencies
GET /health/ready    # can safely take assigned traffic/work
GET /health/status   # detailed diagnostics — authenticated only
```

API node fails readiness on: incompatible schema; unavailable signing; invalid config; deployment fence blocking public operations; release outside cluster compatibility window. Worker fails readiness if it can't safely claim jobs.

`secureintake deployment verify` (structured report, non-zero exit on critical failure):

| Area | Validation |
| --- | --- |
| Application | Expected release version |
| Database | Supported schema |
| Configuration | Valid and consistent |
| Storage | Access and object integrity |
| Signing | Receipt and audit signing operational |
| Workers | Compatible job processing |
| API | Health and protocol capability |
| Portal | Static asset manifest matches release |
| Audit | Checkpoint chain operational |
| Cluster | Nodes on compatible versions |

Preflight output (e.g. `{ "applicationVersion": "...", "securityProfile": "production", "checks": {...}, "ready": true }`) never prints secrets or sensitive connection details and distinguishes fatal failures, warnings and temporary outages.

### Deployment activation record

```json
{ "deploymentId": "dep_prod_01", "release": "4.0.0", "artifactDigest": "sha256:...",
  "configurationGeneration": 42, "databaseSchema": 31, "activatedAt": "2026-11-03T16:00:00Z",
  "activatedBy": "operator_123", "verificationStatus": "passed" }
```

Auditable history — not proof every node updated (that needs node reporting).

## 15. CLI conventions

All operational commands support machine-readable JSON output and these exit codes:

| Code | Meaning |
| --- | --- |
| 0 | Success |
| 2 | Invalid configuration |
| 3 | Authentication or authorisation failure |
| 4 | Dependency unavailable |
| 5 | Migration or schema incompatibility |
| 6 | Security policy violation |
| 7 | Operation failed |
| 8 | Partial completion requiring reconciliation |

Deployment-related commands inspect or manage application state inside already-provisioned resources only:

```bash
secureintake config validate
secureintake database status | migrate | verify
secureintake storage test
secureintake deployment preflight | readiness | verify
```

## 16. Standard update procedure

```bash
secureintake release verify --artifact ... --manifest ... --signature ... --trusted-key ...   # 1 verify
secureintake preflight                                                                        # 2 validate
secureintake database migrate --plan                                                          # 3 review
# 4 operator creates and verifies coordinated backups (external)
secureintake database migrate --apply                                                         # 5 migrate
# 6 operator deploys new binary/image
secureintake deployment verify                                                                # 7 validate
```

Standalone SQLite: maintenance mode with controlled stop/start. Distributed PostgreSQL: the release's rolling-upgrade plan.

## 17. Runbooks (versioned with the app)

Initial installation and bootstrap · DB/storage connection failures · Secret rotation · Upgrades · Failed migrations · Backup restoration · Tenant migration · Audit integrity failure · Compromised API credentials · Compromised encryption keys · Storage-provider outages · Worker backlogs · Organisation suspension and offboarding.

## 18. Data model

```text
deployment_release_activations       database_migration_execution_steps
deployment_release_nodes             deployment_verification_runs
deployment_upgrade_plans             deployment_verification_findings
database_migration_history           deployment_rollback_events
```

For compatibility, safety and audit — the app is not a package manager or orchestrator.

## 19. Audit events

```text
release.verified | verification_failed
deployment.upgrade.planned | started | completed | failed
database.migration.started | completed | failed
deployment.rollback.started | completed | failed
deployment.verification.completed | failed
```

Include release digest, schema versions, config generation, operator identity, result. Never secrets.

## 20. Implementation order

1. Configuration schema and validation ([19](19-configuration-and-secrets.md)).
2. Release manifest and compatibility schema.
3. Native AOT build matrix; standalone packaging.
4. Multi-arch OCI images.
5. Signed artifacts, checksums, SBOM, provenance.
6. Explicit migration CLI; expand-and-contract policy.
7. Health/readiness endpoints; distributed API and worker roles.
8. Versioned worker payloads and compatibility checks.
9. Deployment verification.
10. Upgrade/rollback documentation and runbooks.
