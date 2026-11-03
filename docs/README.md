# Secure Intake: Documentation

This folder turns the architecture planning transcript (versions v1.8 to v4.7; formerly `notes.txt`, removed from the repo, still in git history at commit `6db2a4d`) into topic documents. On 2026-11-03 every open question was resolved, and the missing foundations (v1.0 to v1.7) and the lost end of v4.7 were planned fresh. The docs are now ready to plan implementation from.

## Start here

1. **[BUILD-STATUS.md](BUILD-STATUS.md)**: what code exists so far, what's verified, and what's flagged for review.
2. **[REVIEW.md](REVIEW.md)**: the planning cover note. What was decided, what to check first, and what only you can do.
3. [architecture/01-overview.md](architecture/01-overview.md): what the product is, non-goals, system context, glossary.
4. [architecture/00-foundations.md](architecture/00-foundations.md): base decisions (stack, modules, IDs, dropbox, revisions, audit, jobs, profiles).
5. [architecture/25-implementation-roadmap.md](architecture/25-implementation-roadmap.md): the v1.0 scope and milestones M0 to M4.
6. [DECISIONS.md](DECISIONS.md): one-line log of every decision. [QUESTIONS.md](QUESTIONS.md) is the resolution register.

## Architecture documents

| # | Document | Source |
| --- | --- | --- |
| 00 | [Foundations](architecture/00-foundations.md) | Planned 2026-11-03 (replaces lost v1.0 to v1.7) |
| 01 | [Overview](architecture/01-overview.md) | n/a |
| 02 | [Organisation onboarding and lifecycle](architecture/02-organisation-onboarding-and-lifecycle.md) | v1.8, v4.5 |
| 03 | [Tenancy, isolation and tenant migration](architecture/03-tenancy-and-isolation.md) | v2.0, v4.6 |
| 04 | [Resource governance and quotas](architecture/04-resource-governance-and-quotas.md) | v1.9, v4.7, plus planned sections |
| 05 | [Notifications](architecture/05-notifications.md) | v2.2, v3.4 |
| 06 | [Branding and custom domains](architecture/06-branding-and-custom-domains.md) | v2.3 |
| 07 | [Upload portal distribution](architecture/07-upload-portal-distribution.md) | v2.4 |
| 08 | [Reviewer clients](architecture/08-reviewer-clients.md) | v2.5 |
| 09 | [Key custody, portability and recipients](architecture/09-key-management-and-recipients.md) | v2.6, v2.7 |
| 10 | [Review workflows](architecture/10-review-workflows.md) | v2.8 |
| 11 | [Forms and templates](architecture/11-forms-and-templates.md) | v2.9, v3.0 |
| 12 | [Upload links and invitations](architecture/12-upload-links-and-invitations.md) | v3.1 |
| 13 | [Resumable uploads](architecture/13-resumable-uploads.md) | v3.2 |
| 14 | [Uploader identity](architecture/14-uploader-identity.md) | v3.3 |
| 15 | [Signed receipts](architecture/15-receipts.md) | v3.5 |
| 16 | [Evidence packages](architecture/16-evidence-packages.md) | v3.6 |
| 17 | [Evidence import](architecture/17-evidence-import.md) | v3.7 |
| 18 | [Backup and recovery](architecture/18-backup-and-recovery.md) | v3.8 |
| 19 | [Configuration and secrets](architecture/19-configuration-and-secrets.md) | v3.9 (+ v2.1) |
| 20 | [Deployment, packaging and upgrades](architecture/20-deployment-packaging-and-upgrades.md) | v2.1, v4.0 |
| 21 | [Observability and operations](architecture/21-observability-and-operations.md) | v4.1 |
| 22 | [Access control and JIT elevation](architecture/22-access-control.md) | v4.2 |
| 23 | [Security policy](architecture/23-security-policy.md) | v4.3 |
| 24 | [Key governance](architecture/24-key-governance.md) | v4.4 |
| 25 | [Implementation roadmap](architecture/25-implementation-roadmap.md) | Planned 2026-11-03 |
| 26 | [Cryptographic specifications and review plan](architecture/26-cryptographic-specifications.md) | Planned 2026-11-03 |
| 27 | [Production defaults](architecture/27-production-defaults.md) | Planned 2026-11-03 |
| 28 | [Permission catalogue](architecture/28-permission-catalogue.md) | Planned 2026-11-03 |

## Conventions used in these docs

- Each source-derived doc (02 to 24) opens with a **"Planning decisions applied"** box. Where it disagrees with the text below it, the box wins.
- Code samples (C#, TypeScript, SQL, JSON) are **illustrative contracts**, not final implementations. Production values are in [27](architecture/27-production-defaults.md), not in the samples.
- Product name **Secure Intake**, server CLI **`secureintake`**, client CLI **`secureintake-client`**.
- The notes' v1.8 to v4.7 numbering appears only as a source reference. It's not a product version. Product releases are v1.0, v1.1 and so on ([25](architecture/25-implementation-roadmap.md)).
- New decisions go into ADRs in [`adr/`](adr/README.md).
