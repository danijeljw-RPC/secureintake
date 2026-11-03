# Secure Intake: Architecture Overview

## What it is

Secure Intake is a **self-hostable, multi-tenant, inbound-only secure file-intake platform**. Organisations publish **dropboxes** (intake pages with forms). Uploaders submit files and form data that are **end-to-end encrypted in the browser** for authorised reviewers. The server and storage only ever hold ciphertext for E2EE content. Reviewers decrypt locally in the browser or in an independently distributed desktop/CLI client. Every accepted submission gets an **independently verifiable signed receipt**, and evidence can be exported as **portable, offline-verifiable packages**.

It's a **clean-room build**. No code is taken from any existing project. The implementation stack is **.NET 10 Native AOT** with a **TypeScript (React and Vite)** portal and admin UI ([00-foundations.md](00-foundations.md)). External applications such as Swoovr integrate only through the public API and webhooks. They aren't part of the product.

## Non-goals

- Two-way messaging, document exchange or sending files back to uploaders.
- Provisioning or managing cloud infrastructure, DNS, TLS, backups or secret stores (all operator-owned).
- Auto-updating itself, or acting as a deployment orchestrator or package manager.
- Server-side decryption, server-held reviewer private keys, or server-side plaintext search over E2EE fields.
- User-supplied scripts, SQL or expression languages in policies, workflows or forms.
- Billing. Quotas measure and limit usage; they don't invoice.

## System context

```text
                  ┌──────────────────────────────── Operator-owned infrastructure ───────────────────────────────┐
                  │  DNS · TLS/ingress · PostgreSQL/SQLite · S3/R2/local storage · secret stores · KMS/HSM ·    │
                  │  backups · monitoring (OTLP) · SMTP/email provider                                          │
                  └──────────────────────────────────────────────────────────────────────────────────────────────┘
Uploader browser ──► Public portal (TypeScript; integrated or static) ──► Secure Intake API ──► Tenant data plane
  encrypts locally         │                                            (.NET 10 AOT)         (shared or dedicated DB
                           └── direct ciphertext upload (presigned) ──────────────────────►   + storage per org)
                                                                          │
Reviewer browser / desktop / CLI ◄── ciphertext + envelopes ──────────────┤   Control plane: organisations, routing,
  decrypts locally with own key                                           │   policy, identities, key registry, audit
                                                                          │
Integrations ◄── signed webhooks (metadata only) ─────────────────────────┤
Offline verifier ◄── .sir.json receipts / .siep packages (no server needed)
```

## Building blocks

| Area | Doc |
| --- | --- |
| Organisation onboarding, lifecycle, suspension, deletion | [02](02-organisation-onboarding-and-lifecycle.md) |
| Control plane / data plane, isolation tiers, routing, tenant migration | [03](03-tenancy-and-isolation.md) |
| Hierarchical quotas, reservations, rate limits | [04](04-resource-governance-and-quotas.md) |
| Notifications (email, webhooks, uploader preferences) | [05](05-notifications.md) |
| Branding and custom domains | [06](06-branding-and-custom-domains.md) |
| Public upload portal (frontend, hosting, CSP, encryption integrity) | [07](07-upload-portal-distribution.md) |
| Reviewer clients (browser, desktop, CLI) and file format | [08](08-reviewer-clients.md) |
| Reviewer key custody, portability, recipient assignment | [09](09-key-management-and-recipients.md) |
| Review workflows | [10](10-review-workflows.md) |
| Custom forms, field-level confidentiality, templates | [11](11-forms-and-templates.md) |
| Upload links, invitations, access grants | [12](12-upload-links-and-invitations.md) |
| Resumable uploads and encrypted drafts | [13](13-resumable-uploads.md) |
| Uploader identity | [14](14-uploader-identity.md) |
| Signed receipts | [15](15-receipts.md) |
| Evidence packages (export) | [16](16-evidence-packages.md) |
| Evidence import and restoration | [17](17-evidence-import.md) |
| Backup and recovery | [18](18-backup-and-recovery.md) |
| Configuration and secrets | [19](19-configuration-and-secrets.md) |
| Deployment, packaging, upgrades | [20](20-deployment-packaging-and-upgrades.md) |
| Observability and operations dashboard | [21](21-observability-and-operations.md) |
| Access control, JIT elevation, approvals | [22](22-access-control.md) |
| Hierarchical security policy | [23](23-security-policy.md) |
| Cryptographic key governance | [24](24-key-governance.md) |
| Phased implementation roadmap | [25](25-implementation-roadmap.md) |

## Four separate checks

The notes repeat one rule across every area. Before anyone can do something with a submission, four independent checks must pass:

1. **Authentication**: who you are. Covers built-in identity or OIDC (`issuer + subject`), uploader identities and service accounts.
2. **Authorisation**: what you may do. Covers RBAC, scoped JIT grants, operation approvals, the current effective security policy, tenant scope and legal holds.
3. **Assignment and workflow**: whether you should be handling it.
4. **Cryptographic access**: whether you hold a private key matching one of the submission's recipient envelopes.

No check implies another. Being an admin doesn't let you decrypt. Being assigned doesn't give you an envelope. Having an envelope doesn't let you retrieve the ciphertext. Being authenticated doesn't link you to earlier submissions.

## Deployment shapes

| Mode | Shape |
| --- | --- |
| Standalone | One AOT executable (`serve --role all`), SQLite, local FS or S3, in-process workers. No Redis, broker, Kubernetes or IdP required |
| Container | OCI image with API and worker roles; operator DB and storage |
| Distributed | Multiple API and worker replicas, PostgreSQL (RLS), shared storage, authoritative tenant routing; optional dedicated tenant DBs and buckets |

Profiles: **Development**, **Production** and **Hardened**. Profiles are security baselines. Environment labels such as staging or production are separate from profiles.

## Glossary

| Term | Meaning |
| --- | --- |
| Organisation | A tenant |
| Dropbox | An intake destination with a form, recipient policy, limits and access policy |
| Upload Access Grant | A capability that lets someone start uploading. Types: shared link, invitation, API-issued |
| Upload session / draft | A short-lived, resumable session pinned to form and recipient snapshots |
| Submission / revision | An accepted, immutable intake record. Revisions are append-only snapshots |
| DEK | Per-file (or per-form-payload) data-encryption key |
| Recipient envelope | A DEK wrapped for one recipient public key |
| Recipient snapshot | The exact set of keys selected for an upload session |
| Rewrap | An authorised client re-wraps an existing DEK for a new recipient; the ciphertext is unchanged |
| Re-encryption | An authorised client creates new ciphertext under a new DEK |
| Generation | A monotonic version on a tenant's resource assignment, used for fencing |
| `.sif` | Encrypted file format |
| `.sik` | Encrypted portable private-key package |
| `.sir.json` | Signed submission receipt |
| `.siep` | Portable evidence package (strict ZIP64 profile) |
| Control plane | Deployment-wide registry: organisations, routing, policy, identities, key registry, platform audit |
| Data plane | Tenant data: dropboxes, submissions, envelopes, retention, tenant audit |
