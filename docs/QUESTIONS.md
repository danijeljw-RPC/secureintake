# Questions: Resolution Register

**Every question is now resolved (2026-11-03).** You answered Q-GEN-1 to Q-GEN-7 and delegated the rest. Each delegated decision below was made to be consistent with your answers and the notes. The ones most worth a second look are flagged ⚑ and summarised in [REVIEW.md](REVIEW.md).

Each resolution is recorded in [DECISIONS.md](DECISIONS.md) (Planning decisions) and applied in the docs it names. Future questions go into ADRs (`docs/adr/`), not this file.

---

## 1. Blocking (answered by you)

| ID | Question | Your answer | Resulting plan |
| --- | --- | --- | --- |
| Q-GEN-1 | Missing versions v1.0 to v1.7 | "Don't have them anymore." | Foundations decided fresh in [00-foundations.md](architecture/00-foundations.md) |
| Q-GEN-2 | v4.7 cut off at §6 | "Don't have that unfortunately. Was lost." | Missing sections planned in [04](architecture/04-resource-governance-and-quotas.md) §5 and §9, §15 to §21 |
| Q-GEN-3 | Relationship to Sprag | "Sprag is not needed. We are rebuilding from scratch our own solution." | Clean-room .NET 10 build. No Sprag code, tests or assets. Sprag references removed from the docs. ⚑ `sprag-source/` should be moved out of the workspace (see REVIEW.md) |
| Q-GEN-4 | Naming | "Whatever works. You decide." | **Secure Intake** · `secureintake` · `secureintake-client` · `SECUREINTAKE__` · `.sif/.sik/.sir.json/.siep` ([00](architecture/00-foundations.md) F2). ⚑ Trademark check before public release |
| Q-GEN-5 | Relationship to Swoovr | "Swoovr is a separate application that will use this via API. It's not to be considered part of the app." | Swoovr is a generic API consumer. v1.0 includes API clients, `api_issued` grants, webhooks and `externalReference`. No Swoovr-specific code ([00](architecture/00-foundations.md) F1) |
| Q-GEN-6 | MVP cut | "You decide and plan." | v1.0 scope and milestones M0 to M4 (≈ 32 weeks) in [25](architecture/25-implementation-roadmap.md) |
| Q-GEN-7 | Crypto specs and review | "You decide and plan." | 10 specs, suites, libraries and external review plan in [26](architecture/26-cryptographic-specifications.md). ⚑ You need to select and contract the reviewer |

## 2. Conflicts in the notes

| ID | Resolution | Doc |
| --- | --- | --- |
| Q-ONB-1 | v4.5 lifecycle confirmed. A v1.8 "registration" **is** a v4.5 provisioning request (one entity, `organisation_provisioning_requests`). v1.8 tables dropped | [02](architecture/02-organisation-onboarding-and-lifecycle.md) |
| Q-ONB-2 | v4.5's four suspension modes confirmed | [02](architecture/02-organisation-onboarding-and-lifecycle.md) |
| Q-ONB-3 | **Security lockdown invalidates sessions and upload sessions immediately.** Presigned URLs can't be recalled, so finalisation is rejected and orphans are cleaned up. Other suspension modes follow v4.5 (presigned URLs run to expiry, finalisation rechecks) | [02](architecture/02-organisation-onboarding-and-lifecycle.md) |
| Q-TEN-1 | v4.6 confirmed. Migration is operator-managed only | [03](architecture/03-tenancy-and-isolation.md) |
| Q-TEN-2 | One monotonic **generation** · `organisation_resource_assignments` · `organisations` | [03](architecture/03-tenancy-and-isolation.md) |
| Q-TEN-3 | Dedicated databases are operator-provisioned and platform-administered | [03](architecture/03-tenancy-and-isolation.md) |
| Q-OPS-1 | `secureintake` | [00](architecture/00-foundations.md) F2 |
| Q-CFG-1 | camelCase JSON keys · `SECUREINTAKE__Section__Key` env vars | [19](architecture/19-configuration-and-secrets.md) |
| Q-CFG-2 | **No `Deployment.Mode` key.** Role comes from `serve --role`, and topology is inferred from the database provider. SQLite with more than one running instance is detected with a lock file and lease, and refused | [19](architecture/19-configuration-and-secrets.md), [20](architecture/20-deployment-packaging-and-upgrades.md) |
| Q-DEP-1 | `/health/live`, `/health/ready` and `/health/status` (detailed, authenticated) | [20](architecture/20-deployment-packaging-and-upgrades.md) |
| Q-PRT-1 | Month-granular API version `YYYY-MM` (for example `2026-11`) in the request/response header **`SecureIntake-Api-Version`**. A missing header means the oldest supported version | [07](architecture/07-upload-portal-distribution.md), [20](architecture/20-deployment-packaging-and-upgrades.md) |
| Q-REV-2 | Consolidated on **`/recipient-keys`** (individual, team, organisation and recovery keys). `/reviewer-devices` stays for devices | [08](architecture/08-reviewer-clients.md), [09](architecture/09-key-management-and-recipients.md), [24](architecture/24-key-governance.md) |
| Q-KCR-1 | Confirmed: dropbox default is **Hybrid** (intake team as primary, plus recovery). v1 implements team recipients by expanding the team into individual member envelopes. Shared team keys arrive in v1.2 | [09](architecture/09-key-management-and-recipients.md) |
| Q-FRM-1 | **Templates only.** A "dropbox-only form" is a private template created automatically. The per-dropbox `/forms` endpoints are dropped. v2.9 response tables are kept | [11](architecture/11-forms-and-templates.md) |
| Q-LNK-1 | One public route scheme on every host (platform or custom domain): **`/u/{grantId}#siu_…`** to upload, **`/s/{reference}#sir_…`** for status and receipt (return access). Dropped: `/upload/{id}`, `/d/{publicDropboxId}`, `/u/{org-slug}` | [06](architecture/06-branding-and-custom-domains.md), [12](architecture/12-upload-links-and-invitations.md) |
| Q-UPL-1 | One entity, the **Upload Session** (`upl_…`, table `upload_sessions*`), with one endpoint family, `/api/public/upload-sessions/{id}/…`. A "draft" is an upload session that holds encrypted form state | [13](architecture/13-resumable-uploads.md) |

## 3. Open design questions

### Onboarding and tenancy

| ID | Resolution |
| --- | --- |
| Q-ONB-4 | The offboarding export window is **mandatory**: 30 days by default, 180 days maximum. An Owner can waive it with Security Officer approval. Owners and Security Officers may export. v1.0 exports ciphertext and metadata with the CLI. From v1.1 the export is a `.siep` |
| Q-ONB-5 | (Self-service is v1.3.) For the first 14 days a new organisation is limited to: 3 dropboxes, 5 GiB storage, no custom domain, webhooks only to verified domains, and 50 outbound emails per day. An operator can lift the limits early |
| Q-ONB-6 | **Billing stays out of scope permanently.** The usage export API ([04](architecture/04-resource-governance-and-quotas.md) §19) is the hook for external billing |
| Q-TEN-4 | BYO tenant database: **not planned.** BYO storage: v1.2 |
| Q-TEN-6 | Read-only reviewer access is **allowed by default** while the source is frozen, and blocked during cutover. An organisation can disable it |
| Q-TEN-7 | The SQLite → PostgreSQL migration utility ships in **v1.1** |

### Resources

| ID | Resolution |
| --- | --- |
| Q-RES-2 | Active dropbox count: enforced in v1.0. Webhooks: rate limit with backpressure in v1.0. Bandwidth: reporting only from v1.2 |
| Q-RES-4 | The critical threshold **alerts only** by default. Intake is blocked at 100%. An organisation can opt into restricting intake at critical |
| Q-RES-6 | The series is closed. Future decisions are ADRs (`docs/adr/`) |

### Operations

| ID | Resolution |
| --- | --- |
| Q-BAK-1 / Q-ACC-3 ⚑ | Break-glass uses **FIDO2 hardware security keys registered as emergency credentials** (at least 2, held separately in sealed custody). They work only through `secureintake recovery …` run on the host, so host access and the credential are both required. Hardened requires assertions from **2 distinct custodians**. The quorum is achieved by multiple signatures, so no Shamir scheme is needed. The emergency journal is an append-only local file in which each entry is hash-chained and signed with the audit checkpoint key. It's merged into audit when the database is available |
| Q-BAK-2 | Restore drills are **not enforced**. A readiness warning appears if no drill is recorded within 180 days (Hardened: 90 days) |
| Q-CFG-3 | v1.0 ships env and protected-file providers. Next: HashiCorp Vault and OpenBao (v1.1), then Azure Key Vault and AWS Secrets Manager (v1.2) |
| Q-DEP-2 | **Production servers run on Linux x64 and arm64 only.** Windows x64 and macOS arm64 are for standalone, dev and the client CLI. macOS x64 is dropped. Support: the latest minor gets fixes, and one **LTS** release a year is supported for 18 months |
| Q-DEP-3 ⚑ | **Both:** Sigstore keyless signing (cosign via CI OIDC, logged in Rekor) for transparency, plus a project release key (ECDSA P-256 on a hardware token) that signs `release-manifest.json` for air-gapped verification. Registry: **ghcr.io**. **You hold the release key** |
| Q-OBS-1 | "Restricted" means the opt-in flag `telemetry.includeTenantIdentifiers` (default `false`). When enabled, IDs are **HMAC-pseudonymised** with a rotatable deployment telemetry key. Raw IDs are allowed only on the Development profile |

### Access and policy

| ID | Resolution |
| --- | --- |
| Q-ACC-1 | Draft catalogue in [28-permission-catalogue.md](architecture/28-permission-catalogue.md) |
| Q-ACC-2 | Roles are complete for v1, with the **Provisioning Client** platform service role added. No custom roles in v1 |
| Q-ACC-4 | **v1.0 has both.** Built-in identity (passkeys preferred, password+TOTP allowed, no SMS) and OIDC. Standalone must work without an IdP |
| Q-POL-2 | Lockout-policy exceptions need a platform Owner **and** a Security Admin (2 distinct approvers). They're time-boxed (90 days max), auto-expire and are audited, stored as `policy_exceptions` records |

### Cryptography and keys

| ID | Resolution |
| --- | --- |
| Q-KEY-1 ⚑ | E2EE `SI-E2EE-1` = **hybrid X25519 + ML-KEM-768** (X-Wing combiner), HKDF-SHA-256 and AES-256-GCM chunks. Signing `SI-SIG-1` = **ES256**. No PQ signatures in v1. Fallback if review rejects browser ML-KEM: X25519-only now, with hybrid as suite 2 ([26](architecture/26-cryptographic-specifications.md) §2) |
| Q-KEY-2 ⚑ | **Organisation Key Directory.** The Security Officer's client signs recipient-set snapshots, and links pin the OKD fingerprint in the fragment. Optional in Production, required in Hardened. The residual risk from malicious portal JS is documented ([26](architecture/26-cryptographic-specifications.md) §5) |
| Q-KEY-3 | Threshold recovery is **deferred**. v1 uses multiple independent recovery recipients plus a platform approval quorum |
| Q-KCR-2 | Recovery recipient **required by default**. An Owner may opt out in Production with a recorded acknowledgement. No opt-out in Hardened |
| Q-PRT-3 | WebCrypto plus `@noble/post-quantum` (pinned, vendored) for ML-KEM, and `hash-wasm` for Argon2id. .NET uses the BCL plus a reviewed X25519/Argon2 library ([26](architecture/26-cryptographic-specifications.md) §3) |
| Q-REV-3 | v1.0 previews: PNG, JPEG, static GIF, WebP and plain text (rendered as text). Sandboxed PDF.js arrives in v1.1. **Never previewed:** SVG, HTML and Office files. Those are download only |
| Q-RCP-1 | TSA is deferred (optional in v1.2). Hardened v1 instead **requires the external audit checkpoint sink** |
| Q-RCP-2 | No PDF receipt in v1. A printable HTML receipt view covers it. A PDF library will be evaluated for AOT later |
| Q-EVD-2 | Confirmed: evidence package signing is a separate key domain, held by the deployment |
| Q-IMP-1 | Foreign issuer trust is entered as a manual fingerprint, confirmed out of band, and needs JIT plus 1 Security Officer approval (Hardened: 2). It's stored with a validity period in the trust registry |

### Product and UX

| ID | Resolution |
| --- | --- |
| Q-GEN-8 | **React, Vite, TypeScript and Tailwind** for both the portal and the admin UI. The portal has a strict dependency budget. The desktop GUI is **Avalonia** |
| Q-NOT-1 | `SI-XXXXX-XXXXX`: a deployment-configurable prefix and 10 Crockford base32 characters (9 random plus 1 check). Unique **per deployment** ([00](architecture/00-foundations.md) F9) |
| Q-NOT-2 | SMTP in v1.0. Amazon SES and Microsoft Graph adapters in v1.1. Everything else goes through an SMTP relay |
| Q-NOT-3 | **HMAC-SHA256 only** in v1, in the Standard Webhooks header format. Asymmetric signing comes later |
| Q-NOT-4 | **en-AU (default), en-GB and en-US** at launch, using ICU MessageFormat |
| Q-BRD-2 | Resolved by Q-LNK-1: **no organisation slugs in public URLs and no landing page that lists dropboxes** |
| Q-WF-1 | Internal notes are encrypted for **the submission's current recipient set plus the organisation recovery recipient**, at the time the note is written. New recipients get access through rewrap, the same as files |
| Q-WF-2 | `create_internal_task` is **removed** from the automation catalogue. There's no task entity |
| Q-WF-3 | Uploaders **never see an outcome**. Public states are only Received → In review → Completed, plus "More information requested" when an organisation enables it |
| Q-FRM-2 | Padding is **on by default**, with buckets of 1, 4, 16, 64 and 256 KiB, then 256 KiB multiples. Files aren't padded |
| Q-EVD-1 | Yes, as an optional `.siep` extension in **v1.3**. Notes stay E2EE inside the package |
| Q-UID-1 | v1 is **anonymous and one-off verified only**. Reusable identities come in v1.2. Telephone and SMS verification aren't planned |

## 4. Production defaults

All values are set in [27-production-defaults.md](architecture/27-production-defaults.md): Q-ONB-7, Q-TEN-5, Q-RES-3, Q-RES-5, Q-OBS-2, Q-POL-1, Q-KCR-3, Q-BRD-1, Q-LNK-2 and Q-UPL-2.
