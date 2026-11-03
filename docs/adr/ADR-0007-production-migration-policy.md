# ADR-0007: Production database migrations are explicit and operator-controlled

## Status

**Accepted** (2026-11-03).

## Context

Doc 20 requires operator-controlled upgrades. Automatic schema changes during startup or recovery can corrupt state in rolling deployments. They also let the runtime credentials alter the schema.

## Decision

1. **No implicit migration outside Development.** `serve` applies migrations automatically only under the Development profile, which itself refuses `Environment=production`. In every other profile, `serve` never migrates, repairs or alters the schema. That covers startup, restart and recovery.
2. **Explicit command.** `secureintake migrate` applies migrations and `secureintake migrate status` reports state. Both are privileged CLI verbs: listed operators only (ADR-0004), audited.
3. **Separate credentials.**
   - On PostgreSQL outside Development, `migrate` requires `Database:MigrationConnectionString`. It must be an `env:`/`file:` reference and must differ from the runtime `Database:ConnectionString`.
   - The runtime role (`secureintake_app`) has DML only. Migration 0002 revokes `CREATE` on the schema from `PUBLIC` and from the runtime role.
   - At startup and on every readiness check, `RuntimeRoleCheck` reports any of: superuser, `BYPASSRLS`, `CREATEDB`, `CREATEROLE`, schema `CREATE`, owning tables. Outside Development a finding makes the service **Not Ready**.
4. **Compatibility check.** `serve` checks the schema at startup and on readiness, and reports Not Ready for each of these states:

   | State | Reason code |
   | --- | --- |
   | Migrations pending | `schema.migrations_pending` |
   | Never migrated | `schema.uninitialised` |
   | Migrated by a newer build | `schema.newer_than_build` |
   | Applied script changed | `schema.checksum_mismatch` |
   | Database unreachable | `database.unreachable` |

   Diagnostics give reason codes and version numbers only, never connection details. They go to the startup log, `/health/status` (authorised) and `migrate status`. `/health/ready` stays status-only.
5. **Versioned, ordered, tracked, all-or-nothing.**
   - Migrations are embedded, numbered and checksummed.
   - Each runs in one transaction together with its `schema_migrations` row and a `schema.migration_applied` platform audit record, so a failure leaves nothing partially applied (tested on SQLite and PostgreSQL).
   - Migrations MUST be transactional: no `CREATE INDEX CONCURRENTLY` or other non-transactional DDL. A future need for it gets its own ADR.
6. **Run log.** `schema_migration_log` records every attempt: version, name, checksum, operator, start, finish, and an outcome of `started`, `succeeded` or `failed`. The error is recorded as exception class and SQLSTATE only. A `started` row with no outcome marks an interrupted run. The database rolled it back, `migrate status` reports it, and re-running `migrate` applies it.
7. **No concurrency.** A PostgreSQL session advisory lock (key derived from SHA-256 of `secureintake.migrate`), or an exclusive lock file beside the SQLite database, prevents concurrent runs. A second runner fails immediately with `MigrationLockedException`. Applied versions are re-read under the lock before each step.
8. **Audit.** Every applied migration records the operator identity (`user@host`, or `auto-migrate:development:user@host`) in the run log and the platform audit chain.

## Consequences

- Upgrades are a two-step operator procedure: `migrate` with owner credentials, then roll out the new build. Doc 20's rolling-upgrade guidance must say so.
- SQLite has no roles. Its equivalents are file ownership, the operator allowlist and the lock file.
- A schema created by a newer build stops older builds from becoming ready. That's deliberate (no downgrade).
