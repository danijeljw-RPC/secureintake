# Permission Catalogue (v1 draft)

Status: **planning decision, 2026-11-03.** Answers Q-ACC-1 and Q-ACC-2.

This is the starting catalogue. It becomes `permissions.json`, a generated, versioned catalogue in `SecureIntake.Contracts`. Adding a permission is a reviewed change and needs an ADR when the permission is Sensitive.

**Levels** ([22](22-access-control.md) §2):

- **S**: Standard. The base role is enough.
- **E**: Elevated. Base role plus a JIT grant.
- **X**: Sensitive. Base role, JIT, and independent approval, with approver counts in [27](27-production-defaults.md).

No permission grants decryption. Cryptographic access always needs a recipient envelope.

## Platform scope

| Permission | Level | Owner | Admin | SecAdmin | Operator | Auditor | Provisioning Client |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `platform.dashboard.view` | S | ✓ | ✓ | ✓ | ✓ | ✓ | |
| `platform.organisations.view` | S | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| `platform.organisations.provision` | E | ✓ | ✓ | | | | ✓ (API, pre-approved scope) |
| `platform.organisations.approve` | E | ✓ | ✓ | | | | |
| `platform.organisations.suspend` | E | ✓ | ✓ | ✓ | | | |
| `platform.organisations.delete` | X | ✓ | | | | | |
| `platform.quotas.manage` | E | ✓ | ✓ | | | | |
| `platform.routing.manage` (assignments, migration fences) | X | ✓ | | | ✓ | | |
| `platform.policy.view` | S | ✓ | ✓ | ✓ | | ✓ | |
| `platform.policy.manage` (deployment baseline) | X | ✓ | | ✓ | | | |
| `platform.policy.grant_exception` | X | ✓ | | ✓ | | | |
| `platform.keys.view` | S | ✓ | ✓ | ✓ | | ✓ | |
| `platform.keys.manage_signing` (activate, rotate, revoke) | X | ✓ | | ✓ | | | |
| `platform.trust.manage` (trust registries, foreign issuers) | X | ✓ | | ✓ | | | |
| `platform.operations.jobs.view` | S | ✓ | ✓ | | ✓ | | |
| `platform.operations.jobs.manage` (retry, cancel, replay dead) | E | ✓ | | | ✓ | | |
| `platform.operations.diagnostics.export` | E | ✓ | | | ✓ | | |
| `platform.operations.maintenance` (fence, checkpoint, restore validate) | E | ✓ | | | ✓ | | |
| `platform.audit.view` | S | ✓ | | ✓ | | ✓ | |
| `platform.audit.export` | E | ✓ | | ✓ | | ✓ | |
| `platform.members.manage` | E | ✓ | ✓ | | | | |
| `platform.service_accounts.manage` | X | ✓ | | ✓ | | | |
| `platform.emergency.activate` | Recovery procedure | ✓ | | ✓ | | | |

## Organisation scope

| Permission | Level | Owner | Admin | Dropbox Mgr | Reviewer | SecOfficer | Auditor | API Client |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `org.settings.view` | S | ✓ | ✓ | ✓ | | ✓ | ✓ | |
| `org.settings.manage` | E | ✓ | ✓ | | | | | |
| `org.members.manage` | E | ✓ | ✓ | | | | | |
| `org.teams.manage` | S | ✓ | ✓ | | | | | |
| `org.branding.manage` | S | ✓ | ✓ | | | | | |
| `org.domains.manage` | E | ✓ | ✓ | | | | | |
| `org.policy.manage` (strengthening overrides) | E | ✓ | | | | ✓ | | |
| `org.retention.manage` | E | ✓ | | | | ✓ | | |
| `org.legal_hold.place` | E | ✓ | | | | ✓ | | |
| `org.legal_hold.release` | X | ✓ | | | | ✓ | | |
| `org.dropboxes.view` | S | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| `org.dropboxes.manage` | S | ✓ | ✓ | ✓ | | | | ✓* |
| `org.forms.manage` | S | ✓ | ✓ | ✓ | | | | |
| `org.workflows.manage` | S | ✓ | ✓ | ✓ | | | | |
| `org.grants.manage` (links, invitations) | S | ✓ | ✓ | ✓ | | | | ✓* |
| `org.grants.issue_api` (`api_issued` grants) | S | | | | | | | ✓* |
| `org.submissions.view_metadata` | S | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓* |
| `org.submissions.retrieve_ciphertext` | S | | | | ✓ | | | ✓* |
| `org.submissions.review` (transition, notes, assign self) | S | | | | ✓ | | | |
| `org.submissions.assign` | S | ✓ | ✓ | | ✓ (lead) | | | |
| `org.submissions.request_more_info` | S | | | | ✓ | | | |
| `org.submissions.delete` | X | ✓ | | | | ✓ | | |
| `org.keys.view` | S | ✓ | ✓ | | ✓ (own) | ✓ | ✓ | |
| `org.keys.enrol_own` | S | | | | ✓ | ✓ | | ✓* |
| `org.keys.policy.manage` | E | ✓ | | | | ✓ | | |
| `org.keys.directory.sign` (OKD snapshot) | E | ✓ | | | | ✓ | | |
| `org.keys.revoke` | E | ✓ | | | | ✓ | | |
| `org.keys.export_authorise` (`.sik`) | X | | | | ✓ (own) | ✓ | | |
| `org.keys.rewrap` (add recipient to existing files) | E | | | | ✓ | ✓ | | |
| `org.recovery.activate` | X | ✓ | | | | ✓ | | |
| `org.evidence.export` | X | ✓ | | | ✓ | ✓ | | |
| `org.evidence.export_bulk` | X | ✓ | | | | ✓ | | |
| `org.evidence.import_archive` | E | ✓ | ✓ | | | ✓ | | |
| `org.evidence.import_restore` | X | ✓ | | | | ✓ | | |
| `org.notifications.manage` | S | ✓ | ✓ | ✓ | | | | |
| `org.webhooks.manage` | E | ✓ | ✓ | | | | | ✓* |
| `org.usage.view` | S | ✓ | ✓ | ✓ | | ✓ | ✓ | ✓* |
| `org.audit.view` | S | ✓ | | | | ✓ | ✓ | |
| `org.audit.export` | E | ✓ | | | | ✓ | ✓ | |
| `org.service_accounts.manage` | X | ✓ | | | | ✓ | | |

**✓\*** means API Client permissions are never implicit. Each API client gets an explicit subset at creation, and that subset can't include any Sensitive permission. A typical integrator scope (Swoovr-style) is `org.dropboxes.view`, `org.grants.issue_api`, `org.submissions.view_metadata`, `org.webhooks.manage` and `org.usage.view`. Retrieving ciphertext is optional, and decrypting still needs the integrator's own recipient key.

## Roles (Q-ACC-2)

The roles in [22](22-access-control.md) are confirmed as complete for v1, with one addition: the **Provisioning Client** platform service role. Custom roles aren't in v1. They're reconsidered once the catalogue has been stable in production.
