# ADR-0004: Service accounts hold service roles only and never bypass JIT

## Status

**Accepted** (2026-11-03), with the clarifications below.

## Context

Service accounts authenticate with long-lived bearer tokens. They can't perform JIT elevation, MFA or independent approval. If a service account could exercise an Elevated or Sensitive permission, it would bypass the human controls of doc 22.

## Decision

1. **No JIT bypass.** A service-account principal is denied every permission above Standard: Elevated, Sensitive, and recovery-procedure. This holds regardless of stored roles, stored explicit permissions, claimed elevations or approvals. The authoriser (`Authorizer.EvaluateServiceAccount`) returns `Denied` with reason `elevation_not_available_to_service_accounts`.
2. **Service roles only.**
   - Platform service accounts may hold only `provisioning_client`.
   - Organisation service accounts hold only `api_client`, plus an explicit subset of Standard-level permissions eligible for that role.
   - Any other stored role is ignored for a service-account principal.
3. **One enforcement path.** Account creation goes through `ServiceAccountStore`, which rejects disallowed roles and permissions. The host CLI calls the same store and adds no rules of its own, so the CLI and the API enforce identical restrictions.
4. **The catalogue states it.** `tools/gen_permissions.py` removes non-Standard permissions from the `provisioning_client` and `api_client` grants. It records them in each role's `blockedForServiceAccounts` list in `permissions.json`.
5. **Host CLI for bootstrap.** The host CLI may be used for initial platform administration. Privileged verbs (`migrate`, `organisations`, `service-accounts`, `audit`):
   - require the invoking OS account to be listed in `Cli:AuthorisedOperators` outside Development (refused with exit code 5 otherwise; the list is required in Production and Hardened)
   - record a `host_cli.command` platform audit event with the operator identity (`user@host`), verb, outcome and error code
   - tag every business event they cause with the same `system:host-cli:user@host` actor
6. **After M1 identity.** Once human authentication exists, normal platform administration uses authenticated human identities with JIT and approvals. The CLI stays available for controlled bootstrap, maintenance and recovery.
7. **Health endpoints.** `/health/status` stays protected by `platform.dashboard.view`. Infrastructure probes use `/health/live` and `/health/ready`, which are unauthenticated and return status only. Readiness reasons appear only on `/health/status`, in the logs and in `secureintake migrate status`.

## Consequences

- Two doc 28 grants are now **blocked** for service accounts:

  | Role | Blocked permission | Doc 28 level | Effect |
  | --- | --- | --- | --- |
  | Provisioning Client | `platform.organisations.provision` | E | Can't create organisations over the API. It can still list and view them |
  | API Client | `org.webhooks.manage` | E | An integrator can't register its own webhooks. This affects the M2 Swoovr exit criterion |

  ⚑ **Decision needed before M2.** A recommended resolution, not implemented:
  - Add narrowly scoped Standard permissions for the automated paths, and keep the human permissions Elevated:
    - `platform.organisations.request`: creates an organisation in `pending_approval`, never active
    - `org.webhooks.manage_own`: only endpoints owned by the calling client, with SSRF controls
  - Leave activation and approval with humans.
  - This is a catalogue change (doc 28) needing its own ADR.
- Until human identity lands in M1, platform administration over HTTP isn't possible; the host CLI is the admin path.
- `Cli:AuthorisedOperators` is an accountability control on top of OS permissions. Anyone who can read the configuration and secrets can bypass it. Deployment guidance must restrict the config, secrets and data directory to the operator group.
