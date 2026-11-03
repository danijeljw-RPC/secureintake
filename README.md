# Secure Intake

Self-hosted, API-first, multi-tenant secure file intake with server-blind end-to-end encryption. Clean-room .NET 10 Native AOT build. Design docs: [docs/README.md](docs/README.md). Current state of the code: [docs/BUILD-STATUS.md](docs/BUILD-STATUS.md).

> **Pre-review cryptography.** The `SI-E2EE-1` suite hasn't passed external review. The Production and Hardened profiles refuse to start until it has (doc 26 §1). Use the Development profile only, and never with real data.

## Build and test

Requires the .NET 10 SDK (see `global.json`).

```sh
dotnet build SecureIntake.slnx
dotnet test SecureIntake.slnx
```

- **PostgreSQL isolation suite.** Set `SECUREINTAKE_TEST_POSTGRES` to a superuser connection string and create the runtime role first: `CREATE ROLE secureintake_app LOGIN PASSWORD 'app' NOSUPERUSER NOBYPASSRLS;`. Without the variable, only SQLite runs.
- **Hybrid KEM tests** need ML-KEM from the platform: Windows, or Linux with OpenSSL 3.5+. They skip on macOS. `mcr.microsoft.com/dotnet/sdk:10.0-alpine` runs them.
- **TypeScript crypto-protocol** (independent SI-E2EE-1 and SI-JCS-1 implementation, WebCrypto only): `cd frontend/packages/crypto-protocol && npm test` (Node 23.6+, no dependencies). It verifies the same vector files as .NET.
- **Test vectors** live in `docs/specs/vectors/`. See [docs/specs/README.md](docs/specs/README.md) for how to regenerate them.
- **Permission catalogue.** `src/SecureIntake.Contracts/Catalogue/permissions.json` is generated from doc 28: run `python3 tools/gen_permissions.py` after editing the doc.

## Run locally (Development profile)

```sh
export SECUREINTAKE__Profile=Development
dotnet run --project src/SecureIntake.Host -- migrate
dotnet run --project src/SecureIntake.Host -- organisations create --name "Acme Legal" --slug acme --activate
dotnet run --project src/SecureIntake.Host -- service-accounts create-platform --name provisioner --roles provisioning_client
dotnet run --project src/SecureIntake.Host -- serve            # http://127.0.0.1:8080
dotnet run --project src/SecureIntake.Host -- audit verify
```

Development keeps its database, token pepper and audit-checkpoint key in `./.secureintake/` (git-ignored). `secureintake help` lists every verb.

Outside Development:

- **Migrations are never automatic** (ADR-0007). Run `secureintake migrate` with `Database:MigrationConnectionString` (the schema owner), and check with `secureintake migrate status`. `serve` reports Not Ready until the schema matches.
- **Privileged CLI verbs** (`migrate`, `organisations`, `service-accounts`, `audit`) require the OS account in `Cli:AuthorisedOperators`, and are audited (ADR-0004).

## Native AOT

```sh
dotnet publish src/SecureIntake.Host -c Release -r linux-x64   # or linux-arm64, osx-arm64, win-x64
```

The output is a single `secureintake` executable plus `libsodium` and `e_sqlite3` native libraries.

## Layout

```text
src/SecureIntake.Domain          entities, state machines, IDs (no I/O)
src/SecureIntake.Contracts       DTOs and the generated permission catalogue
src/SecureIntake.Cryptography    SI-E2EE-1, JCS, audit chain, ES256 (no server I/O)
src/SecureIntake.Application     use cases and the authoriser
src/SecureIntake.Infrastructure  SQL (SQLite and PostgreSQL), audit store, jobs, secrets, tokens
src/SecureIntake.Api             minimal-API endpoints, problem+json, version header
src/SecureIntake.Worker          job dispatcher, checkpoint scheduler
src/SecureIntake.Host            composition root and host CLI (`secureintake`)
frontend/packages/crypto-protocol  TypeScript SI-E2EE-1 / SI-JCS-1 (WebCrypto), for the M2 portal
tests/                           unit, integration (SQLite + PostgreSQL) and architecture tests
docs/                            architecture, decisions, ADRs, specs and test vectors
```
