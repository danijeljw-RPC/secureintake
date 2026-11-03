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

Development keeps its database, token pepper and audit-checkpoint key in `.secureintake/` under the working directory (git-ignored). `dotnet run` starts the host in its project folder, so that is `src/SecureIntake.Host/.secureintake/`. Set `SECUREINTAKE__DataDirectory` to put it elsewhere, and use the same value for every command. `secureintake help` lists every verb. If port 8080 is taken, `serve` exits with code 6. Pass `--Http:Urls=http://127.0.0.1:8081` to use another port.

### What works today

**There is no web UI yet.** The upload portal (React) arrives in M2, and in-browser reviewing in M3 (roadmap, doc 25). Today you can run:

- the host CLI
- the HTTP API: platform organisations, dropboxes and health
- the audit trail

To try the API, run `serve` in one terminal and the following in another:

```sh
si() { dotnet run --project src/SecureIntake.Host -- "$@"; }
ORG=$(si organisations list | awk '/acme/{print $1}')
TOKEN=$(si service-accounts create-api-client --organisation "$ORG" --name demo \
          --permissions org.dropboxes.view,org.dropboxes.manage | awk '/^token:/{print $2}')
API=http://127.0.0.1:8080
H=(-H "Authorization: Bearer $TOKEN" -H "SecureIntake-Api-Version: 2026-11")

curl -s $API/health/ready                                    # {"status":"ready"}
curl -s "${H[@]}" -H 'Content-Type: application/json' \
     -d '{"name":"Client documents","slug":"client-docs"}' $API/api/organisations/$ORG/dropboxes/
curl -s "${H[@]}" $API/api/organisations/$ORG/dropboxes/
```

The run step above also creates a platform `provisioner` token. With it, you can list organisations at `GET /api/platform/organisations/`. Creating one through the API returns 403 for now: see ADR-0004 and the open decision in [BUILD-STATUS](docs/BUILD-STATUS.md).

The cryptography is built and tested but nothing calls it yet. SI-E2EE-1 in .NET and TypeScript has no upload or download path; that is M2.

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
