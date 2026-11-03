# Implementation Roadmap

Status: **planning decision, 2026-11-03.** Answers [Q-GEN-6](../QUESTIONS.md). Replaces the earlier proposal. Durations are rough estimates for a small team (2 to 3 engineers) and exist to support sequencing, not commitments.

## Guiding rules

1. **Shared tenancy first**, with hybrid routing abstractions from day one. No service may assume a single database connection ([03](03-tenancy-and-isolation.md)).
2. **CLI reviewer client before browser decryption** ([08](08-reviewer-clients.md)).
3. **No production release on unreviewed crypto** ([26](26-cryptographic-specifications.md)).
4. **The API is the product.** Every admin capability ships as API first, then CLI, then UI. Integrators such as Swoovr are first-class from M2 ([00](00-foundations.md) F1).
5. Build each module's tenant-isolation, authorisation and audit tests in the same milestone as the feature, never afterwards.

## v1.0 scope

**v1.0 delivers a self-hosted, multi-tenant (shared tier), API-first secure intake service with browser E2EE upload, CLI and browser review, signed receipts, governance basics, and safe operations.**

| Area | In v1.0 | Deferred |
| --- | --- | --- |
| Deployment | Standalone (SQLite) and container/distributed (PostgreSQL with RLS) · Linux x64/arm64 production · Windows x64 and macOS arm64 for standalone and dev · signed releases, SBOM, OCI | Windows ARM64 · macOS x64 (dropped) |
| Tenancy | Shared tier, routing registry, generations | Dedicated DB/storage tiers and tenant migration (v1.2) · SQLite→PostgreSQL migration utility (v1.1) · BYO storage (v1.2) |
| Onboarding | Manual, CLI and API provisioning · initial admin invitation · activation gates | Self-service onboarding (v1.3) |
| Identity | Built-in identity (passkeys plus password+TOTP) **and** OIDC · service accounts | Uploader reusable identities (v1.2) |
| Access | RBAC · JIT elevation · approvals · deployment baseline and organisation strengthening policy | Custom roles |
| Intake | Dropboxes · shared links · invitations (email-bound) · `api_issued` grants · resumable upload (same browser) · form templates with field-level confidentiality · encrypted form payloads | Cross-device draft recovery (v1.2) · standalone static portal (v1.1) |
| Keys | Device-bound reviewer keys · individual envelopes with team expansion · recovery recipients (required by default) · OKD (optional; required in Hardened) · `.sik` export and import in the CLI | Browser `.sik` (v1.1) · shared team keys and epochs (v1.2) · KMS/HSM adapters (v1.2) · threshold recovery (deferred) |
| Review | CLI reviewer client · browser decryption with image and text preview · default workflow · assignments · E2EE internal notes · rewrap (CLI) | Custom workflows and automation (v1.1) · PDF preview (v1.1) · desktop GUI (v1.3) |
| Evidence | Signed JSON receipts · offline verifier CLI · printable HTML receipt view | `.siep` export (v1.1) · import (v1.2) · PDF receipts · TSA timestamps (v1.2 optional) |
| Notifications | SMTP · transactional templates (en-AU, en-GB, en-US) · uploader mandatory notices · webhooks (HMAC, Standard Webhooks format) with SSRF controls | Amazon SES and Microsoft Graph adapters (v1.1) · uploader optional notification preferences (v1.1) · asymmetric webhook signing |
| Governance | Quotas per [04](04-resource-governance-and-quotas.md) §21 v1.0 row · retention policies · legal holds · audit chain with external checkpoint sink | Bandwidth reporting (v1.2) |
| Operations | Health, readiness and `/health/status` · OpenTelemetry · minimal ops dashboard · job administration · recovery checkpoints, fencing and restore validation · break-glass CLI | Full alert rule engine UI (v1.1) · diagnostic bundles (v1.1) |
| Branding | Organisation display name, logo and primary colour | Custom domains and custom email sender (v1.1) |
| Config | Env and protected-file secret providers | HashiCorp Vault/OpenBao (v1.1) · Azure Key Vault and AWS Secrets Manager (v1.2) |

## Milestones

### M0: Specifications and foundations (≈ 6 weeks)

- Write specs S1 to S6, S8 and S10 ([26](26-cryptographic-specifications.md) §4), with test vectors from a throwaway reference implementation.
- **Engage the external crypto reviewer** (critical path, see [REVIEW.md](../REVIEW.md)).
- Threat model document (STRIDE per module plus E2EE adversary model).
- Typed configuration schema · permission catalogue → `permissions.json` ([28](28-permission-catalogue.md)) · security policy catalogue.
- Repository scaffolding: solution layout ([00](00-foundations.md) F4), CI with AOT publish on Linux, macOS and Windows runners, architecture tests, trimming warnings as errors.
- ADR process in `docs/adr/`.

**Exit:** specs frozen and sent for review · CI green on an AOT "hello" host across targets.

### M1: Platform skeleton (≈ 6 weeks)

- Host with `serve --role all|api|worker` · configuration precedence and validation · env and file secrets.
- Control plane and tenant data plane · tenant-scoped repositories · SQLite and PostgreSQL with RLS · **cross-tenant isolation test suite** · routing registry with generations.
- Durable jobs, outbox and fair scheduler ([04](04-resource-governance-and-quotas.md) §9) · audit chain and signed checkpoints with external sink (S8).
- Built-in identity (passkeys, password+TOTP) · OIDC · sessions · CSRF · service accounts.
- RBAC from `permissions.json` · migration CLI (expand and contract) · health endpoints.

**Exit:** two organisations provisioned by CLI, isolation suite green on both databases, audit chain verifiable offline.

### M2: First end-to-end intake slice (≈ 8 weeks) → internal alpha

- Provisioning (manual, CLI, API) · invitations · activation gates.
- Dropboxes · device enrolment with proof of possession · recipient policies (individual plus team expansion plus recovery) · immutable recipient snapshots · OKD signing in the CLI.
- Grants: shared links, invitations, `api_issued` · redemption · access sessions · email OTP and OIDC binding.
- Portal (TypeScript, React, Vite): streaming E2EE (S1 to S3), chunked direct-to-storage upload, server-confirmed chunks, idempotent finalisation, same-browser resume.
- Form templates, field confidentiality and padded encrypted payloads.
- Minimum quotas: reservations, size limits, concurrency leases, rate limits.
- Receipt signing worker (S5, S6) · JSON receipt download · return-access status page.
- **CLI reviewer:** login, list, download, decrypt, verify receipt, `.sik` backup.
- SMTP and templates · webhooks (HMAC, SSRF-safe).
- Browser↔CLI interop tests in CI.

**Exit:** an external integrator (Swoovr as the first test consumer) can, purely through the API, create an `api_issued` grant, receive a `submission.received` webhook, and have a reviewer decrypt the files with the CLI. **Internal alpha** runs on the Development profile with the pre-review crypto banner.

### M3: Review and governance (≈ 6 weeks)

- Default workflow · transition engine · assignments · E2EE internal notes ([10](10-review-workflows.md)).
- Browser reviewer decryption (images and text preview) · CLI rewrap.
- JIT elevation · approval engine · privileged operation authoriser · deployment baseline and strengthening policy.
- Key lifecycle metadata · signing-key rotation · trust registry · offline verifier hardening.
- Retention policies · legal holds · deletion jobs.

**Exit:** all [22](22-access-control.md) §14 security invariants automated and green.

### M4: Operations hardening and release (≈ 6 weeks) → **v1.0**

- Recovery checkpoints · maintenance fencing · restore validation · break-glass CLI with emergency journal.
- OpenTelemetry · ops dashboard · job administration · core alerts.
- Remaining v1.0 quota items · reconciliation jobs.
- Signed releases (Sigstore plus project key), SBOM, provenance, OCI images, compatibility manifest, rolling-upgrade test.
- Crypto implementation review fixes · external penetration test of the portal and API.
- Operator documentation and runbooks.

**Exit (v1.0 gate):** crypto review High and Critical findings closed · pen test High and Critical findings closed · restore drill passed on both databases · upgrade from the previous build passes · trademark check done.

## Post-v1 releases (indicative)

| Release | Theme | Contents |
| --- | --- | --- |
| v1.1 | Evidence and reach | `.siep` export and verifier · custom workflows and automation · custom domains and sender identity · standalone static portal · browser `.sik` · PDF preview · SES and Graph email · Vault/OpenBao secrets · SQLite→PostgreSQL utility · uploader notification preferences · diagnostic bundles · alert rule UI |
| v1.2 | Isolation and scale | Dedicated DB and storage tiers · tenant migration fencing and cutover · BYO storage · KMS/HSM signing and team-key custody · shared team keys and epochs · evidence import (archive, then native restoration) · reusable uploader identities · cross-device drafts · bandwidth reporting · Azure and AWS secret providers · optional RFC 3161 TSA |
| v1.3 | Self-service and desktop | Self-service onboarding with anti-abuse controls · Avalonia desktop reviewer · `.siep` workflow and notes extension |

## Critical path

`Crypto reviewer engaged (M0 start)` → `specs frozen (M0)` → `portal crypto plus CLI interop (M2)` → `implementation review (end M2 to M4)` → `v1.0`.

Total to v1.0 is roughly **32 weeks** from M0 start, assuming the reviewer is booked in the first two weeks.
