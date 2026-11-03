# ADR-0005: PostgreSQL runtime role, RLS and append-only audit grants

## Context

Doc 03 §3 requires that the runtime role not own tables or hold `BYPASSRLS`, and F11 requires append-only audit.

## Decision

- Migrations run as the schema owner (`Database:MigrationConnectionString`, `secureintake migrate`). The application connects as `secureintake_app` (operator-created, `NOSUPERUSER NOBYPASSRLS`).
- Tenant tables (`dropboxes`, `organisation_audit`, `organisation_audit_heads`, and every future tenant table) have `ENABLE` and `FORCE ROW LEVEL SECURITY` with a policy on `current_setting('app.organisation_id', true)`. The setting is applied with `set_config(…, true)`, so it's transaction-local and can't leak through the pool.
- The runtime role gets `SELECT, INSERT` only on `platform_audit`, `organisation_audit` and `audit_checkpoints`. Chain heads are updatable. SQLite uses `BEFORE UPDATE/DELETE` triggers for the same rule.
- The runtime role can read `schema_migrations` but not create it, so readiness checks are read-only.

## Consequences

- Tested: raw SQL under the runtime role sees only the bound tenant's rows, and sees none without a binding; an insert for another tenant violates `WITH CHECK`; `UPDATE platform_audit` fails with `42501`.
- An attacker with owner credentials can still edit history. The chain plus signed checkpoints in an external sink detect that (tested); they can't prevent it.
- Workers that claim jobs across tenants use control-plane tables without RLS. Tenant work inside a job must open a tenant-bound transaction.

## Status

Proposed, 2026-11-03.
