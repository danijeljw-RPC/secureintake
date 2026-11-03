# Decision Log

One line per confirmed architecture decision. The first table is the decisions from `notes.txt` (v1.8 to v4.7). Versions v1.0 to v1.7 were lost and are replaced by the **planning decisions** at the end of this file ([00-foundations.md](architecture/00-foundations.md)). Later decisions supersede earlier ones where they overlap. New decisions are ADRs in `docs/adr/`.

| Ver | Area | Decision | Doc |
| --- | --- | --- | --- |
| v1.8 | Onboarding | Deployment-configurable onboarding: manual, privileged API and self-service (opt-in) with optional approval. *Refined by v4.5.* | [02](architecture/02-organisation-onboarding-and-lifecycle.md) |
| v1.9 | Quotas | Policy-based quotas with deployment defaults and per-organisation limits. Quotas are not billing. *Refined by v4.7.* | [04](architecture/04-resource-governance-and-quotas.md) |
| v2.0 | Tenancy | Shared multi-tenant by default, with optional dedicated DB and/or storage per organisation. Shared control plane, assignable tenant data planes. | [03](architecture/03-tenancy-and-isolation.md) |
| v2.1 | Infrastructure | Secure Intake never creates, buys, resizes or manages cloud infrastructure. The operator supplies it. | [20](architecture/20-deployment-packaging-and-upgrades.md) |
| v2.2 | Notifications | Configurable notification framework. Email is the primary human channel; webhooks for integrations. Never delivers files. | [05](architecture/05-notifications.md) |
| v2.3 | Branding | Organisation branding plus optional verified custom domains. DNS and TLS are operator-managed. | [06](architecture/06-branding-and-custom-domains.md) |
| v2.4 | Portal | One portal build distributed two ways: integrated in the host, or standalone static. | [07](architecture/07-upload-portal-distribution.md) |
| v2.5 | Reviewers | Both browser decryption and independently distributed desktop/CLI clients, on the same protocol. | [08](architecture/08-reviewer-clients.md) |
| v2.6 | Key custody | Organisation policy chooses device-bound (default), encrypted-portable (`.sik`) or externally managed keys. | [09](architecture/09-key-management-and-recipients.md) |
| v2.7 | Recipients | Dropboxes may encrypt for individual reviewers, team recipients or both. Authorisation and cryptographic access stay separate. | [09](architecture/09-key-management-and-recipients.md) |
| v2.8 | Workflows | Organisation-defined, versioned review workflows with assignment, approvals, notes, escalation and constrained automation. | [10](architecture/10-review-workflows.md) |
| v2.9 | Forms | Custom forms with field-level confidentiality: server-readable or E2EE. | [11](architecture/11-forms-and-templates.md) |
| v3.0 | Templates | Reusable, versioned organisation form templates. Dropboxes pin a version and may apply presentation-only overrides. | [11](architecture/11-forms-and-templates.md) |
| v3.1 | Upload access | Shared links plus individual invitations, unified as Upload Access Grants. | [12](architecture/12-upload-links-and-invitations.md) |
| v3.2 | Resumable uploads | Resumable uploads within a bounded period. The server only ever holds ciphertext, never plaintext drafts. | [13](architecture/13-resumable-uploads.md) |
| v3.3 | Uploader identity | Anonymous, one-off verified, or optional reusable identity. Identity never implies authorisation. | [14](architecture/14-uploader-identity.md) |
| v3.4 | Uploader notifications | Organisations choose which notifications are available; uploaders set preferences for optional ones. Mandatory security notices can't be disabled. | [05](architecture/05-notifications.md) |
| v3.5 | Receipts | Every finalised submission gets a signed receipt that can be verified offline (`.sir.json`). | [15](architecture/15-receipts.md) |
| v3.6 | Evidence export | Portable encrypted evidence packages (`.siep`) that can be verified offline. | [16](architecture/16-evidence-packages.md) |
| v3.7 | Evidence import | Two modes, archive-only or controlled native restoration. Original receipts and provenance are preserved. | [17](architecture/17-evidence-import.md) |
| v3.8 | Backup | No built-in backup scheduler or DR system. Secure Intake provides consistency checkpoints, fencing and restore validation. | [18](architecture/18-backup-and-recovery.md) |
| v3.9 | Configuration | Layered configuration with pluggable, compile-time-registered secret providers. | [19](architecture/19-configuration-and-secrets.md) |
| v4.0 | Packaging | Native AOT executables plus OCI images. Updates are operator-controlled; the app never auto-updates. | [20](architecture/20-deployment-packaging-and-upgrades.md) |
| v4.1 | Observability | Built-in operations dashboard plus OpenTelemetry, under a strict telemetry privacy policy. | [21](architecture/21-observability-and-operations.md) |
| v4.2 | Access | RBAC, plus JIT elevation, plus independent approval for sensitive operations. | [22](architecture/22-access-control.md) |
| v4.3 | Policy | Hierarchical security policy. Organisation overrides can only strengthen the deployment baseline. | [23](architecture/23-security-policy.md) |
| v4.4 | Key governance | Built-in key governance supporting local keys, organisation-held E2EE keys and optional KMS/HSM. KMS/HSM is not mandatory. | [24](architecture/24-key-governance.md) |
| v4.5 | Provisioning | One unified provisioning pipeline for manual, API/CLI and self-service onboarding. Supersedes the v1.8 lifecycle. | [02](architecture/02-organisation-onboarding-and-lifecycle.md) |
| v4.6 | Tenant migration | No automatic migration. Operators copy the data; Secure Intake fences, verifies and controls cutover. Supersedes the v2.0 migration engine. | [03](architecture/03-tenancy-and-isolation.md) |
| v4.7 | Resource governance | Hierarchical deployment, organisation and dropbox quotas with durable reservations and fair scheduling. **Notes incomplete.** | [04](architecture/04-resource-governance-and-quotas.md) |

## Cross-cutting principles that recur throughout the notes

- **Inbound-only.** Nothing is ever delivered back to uploaders. More-information requests add a submission revision.
- **Server-blind E2EE.** The server never holds plaintext of E2EE content, private decryption keys or plaintext DEKs.
- **Authorisation ≠ cryptographic access.** Roles, assignments, grants and envelopes are separate records and checks.
- **Operator owns infrastructure.** That includes DNS, TLS, databases, buckets, backups, secrets stores and deployment orchestration.
- **Immutable, versioned, pinned.** Workflows, forms, templates, policies, recipient snapshots and receipts are never edited in place.
- **Durable, idempotent, fenced.** Outbox events, at-least-once delivery, monotonic generations, fail closed when the authoritative state is unreachable.
- **Honest claims.** Separate what is proven from what is asserted (receipt validity vs signer trust vs evidence integrity; provider acceptance vs delivery).
- **No `/v1` URL prefixes.** API compatibility is declared through headers and capability documents.
- **No scripting engines.** Rules use typed, allowlisted catalogues. No user JavaScript, SQL or expressions.
- **Desktop-only for high-risk administration.** The API enforces the same rules regardless of client.

## Planning decisions (2026-11-03)

These resolve every item in [QUESTIONS.md](QUESTIONS.md). P-01 to P-04 follow your direct answers. The rest were delegated. ⚑ marks decisions worth a second look ([REVIEW.md](REVIEW.md)).

| ID | Area | Decision | Doc |
| --- | --- | --- | --- |
| P-01 | Scope | Clean-room .NET 10 Native AOT build. Sprag isn't used and none of its code is reused (Q-GEN-3) | [00](architecture/00-foundations.md) |
| P-02 | Scope | Swoovr is an external API consumer with no special treatment. API clients, `api_issued` grants and webhooks are v1.0 features (Q-GEN-5) | [00](architecture/00-foundations.md) |
| P-03 | Foundations | Missing v1.0 to v1.7 foundations are decided fresh: modules, IDs, dropbox, revisions, return access, retention and legal holds, audit chain, jobs and outbox, profiles (Q-GEN-1) | [00](architecture/00-foundations.md) |
| P-04 | Quotas | The lost v4.7 sections are planned: atomic multi-scope reservation, DRR fair scheduler with priority classes, reconciliation, overrides, reporting (Q-GEN-2) | [04](architecture/04-resource-governance-and-quotas.md) |
| P-05 | Naming | Secure Intake · `secureintake` · `secureintake-client` · `SECUREINTAKE__` (Q-GEN-4) ⚑ | [00](architecture/00-foundations.md) |
| P-06 | Roadmap | v1.0 scope plus milestones M0 to M4 (≈ 32 weeks). Post-v1 releases v1.1 to v1.3 (Q-GEN-6) | [25](architecture/25-implementation-roadmap.md) |
| P-07 | Crypto | 10 formal specs, test vectors and an external review that gates production (Q-GEN-7) ⚑ | [26](architecture/26-cryptographic-specifications.md) |
| P-08 | Crypto | E2EE suite: hybrid X25519 + ML-KEM-768, AES-256-GCM 4 MiB chunks. Signing: ES256 (Q-KEY-1) ⚑ | [26](architecture/26-cryptographic-specifications.md) |
| P-09 | Crypto | Organisation Key Directory against key substitution. Required in Hardened (Q-KEY-2) ⚑ | [26](architecture/26-cryptographic-specifications.md) |
| P-10 | Keys | Recovery recipient required by default. Threshold recovery deferred (Q-KCR-2, Q-KEY-3) | [26](architecture/26-cryptographic-specifications.md) |
| P-11 | Stack | React, Vite, TypeScript and Tailwind for portal and admin. Avalonia desktop. Hand-written SQL, no EF Core (Q-GEN-8) | [00](architecture/00-foundations.md) |
| P-12 | Identity | v1.0 has built-in identity (passkeys, password+TOTP) **and** OIDC (Q-ACC-4) | [25](architecture/25-implementation-roadmap.md) |
| P-13 | Routes | Public routes are `/u/{grantId}#siu_…` and `/s/{reference}#sir_…` only. No organisation slugs (Q-LNK-1, Q-BRD-2) | [12](architecture/12-upload-links-and-invitations.md) |
| P-14 | Uploads | One Upload Session entity and endpoint family (Q-UPL-1) | [13](architecture/13-resumable-uploads.md) |
| P-15 | API | Version header `SecureIntake-Api-Version: YYYY-MM`. `/recipient-keys` consolidated (Q-PRT-1, Q-REV-2) | [20](architecture/20-deployment-packaging-and-upgrades.md) |
| P-16 | Onboarding | Security lockdown invalidates sessions immediately. v4.5 lifecycle and suspension confirmed (Q-ONB-1 to 3) | [02](architecture/02-organisation-onboarding-and-lifecycle.md) |
| P-17 | Ops | Break-glass uses FIDO2 emergency credentials on the host CLI, with 2 custodians in Hardened (Q-BAK-1) ⚑ | [QUESTIONS](QUESTIONS.md) |
| P-18 | Release | Sigstore keyless plus a hardware-held project release key. ghcr.io. Linux-only production servers (Q-DEP-2, Q-DEP-3) ⚑ | [20](architecture/20-deployment-packaging-and-upgrades.md) |
| P-19 | Access | Permission catalogue v1 draft. Provisioning Client role added (Q-ACC-1, Q-ACC-2) | [28](architecture/28-permission-catalogue.md) |
| P-20 | Defaults | All illustrative values replaced with Production and Hardened defaults (QUESTIONS §4) | [27](architecture/27-production-defaults.md) |
| P-21 | Product | No billing ever. Uploaders never see outcomes. No telephone verification. en-AU, en-GB and en-US at launch | [QUESTIONS](QUESTIONS.md) |

## Architecture decision records (from 2026-11-03)

| ADR | Area | Decision |
| --- | --- | --- |
| [0001](adr/ADR-0001-repository-layout-and-build-rules.md) | Build | Solution layout, AOT and warnings-as-errors everywhere, enforced module references, generated permission catalogue |
| [0002](adr/ADR-0002-libsodium-for-x25519.md) | Crypto | X25519 through direct libsodium bindings (NSec can't export the raw shared secret the combiner needs). **Accepted, crypto design reviewed** |
| [0003](adr/ADR-0003-jcs-integer-only-profile.md) | Crypto | SI-JCS-1: canonical JSON accepts plain integer tokens only (±2^53 − 1); fractions, exponents and `-0` are rejected. **Accepted, crypto design reviewed** |
| [0004](adr/ADR-0004-service-accounts-hold-service-roles-only.md) | Access | Service accounts hold only `provisioning_client` or `api_client` and never get Elevated or Sensitive permissions; host CLI restricted to listed operators and audited. **Accepted.** ⚑ Two doc 28 grants are now blocked |
| [0005](adr/ADR-0005-postgres-runtime-role-and-audit-grants.md) | Data | Non-owner runtime role, forced RLS on tenant tables, append-only audit grants |
| [0006](adr/ADR-0006-public-reference-check-symbol.md) | IDs | Public reference check symbol: weighted mod-32 over the nine random symbols |
| [0007](adr/ADR-0007-production-migration-policy.md) | Data | Production migrations only via `secureintake migrate` with owner credentials, locked, logged and audited; `serve` reports Not Ready instead of migrating. **Accepted** |
| S1 | Crypto | Provisional HKDF combiner kept (no X-Wing for now); byte-level S1 candidate values accepted pending review; any ciphertext-affecting change requires a new suite ([S1](specs/S1-si-e2ee-1.md)) |
