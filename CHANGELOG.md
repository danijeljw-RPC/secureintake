# Changelog

All notable changes to Secure Intake are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versions will follow [Semantic Versioning](https://semver.org/) from the first release.

## [Unreleased]

### Added

- **Platform scaffold (M0).** A .NET 10 modular monolith with these modules: Domain, Contracts, Cryptography, Application, Infrastructure, Api, Worker and Host.
  - Native AOT, with warnings treated as errors.
  - Central package management.
  - CI workflow with these jobs: catalogue drift, test matrix, PostgreSQL, ML-KEM, AOT publish and portal crypto.
- **Domain.**
  - Typed UUIDv7 identifiers in Crockford base32.
  - Public references (`SI-XXXXX-XXXXX`) with a check symbol.
  - Organisation lifecycle state machine.
  - Dropbox activation blockers.
- **Data layer.**
  - SQLite and PostgreSQL with hand-written SQL.
  - Forced row-level security per organisation.
  - A non-owner runtime role (`secureintake_app`).
  - Transactional outbox and job store.
- **Migrations (ADR-0007).**
  - Production never migrates implicitly; only `secureintake migrate` applies migrations.
  - `migrate` needs a separate owner connection, takes a concurrency lock, and records each run in a log and an audit record.
  - Each migration applies all-or-nothing.
  - `migrate status` reports the schema state.
  - Interrupted runs are detected.
  - Readiness reason codes.
  - A runtime-role privilege check.
- **Access control (ADR-0004).**
  - Permission catalogue generated from doc 28, with a drift check.
  - Service accounts are limited to Standard-level permissions and can never bypass JIT elevation.
  - Host CLI operator allowlist (`Cli:AuthorisedOperators`), with `host_cli.command` audit events.
- **Audit.**
  - Hash-chained audit records over SI-JCS-1.
  - ES256-signed checkpoints and a JSONL external sink.
  - Chain verifier.
- **SI-JCS-1 (ADR-0003).** A normative canonical JSON profile limited to integers. It is a strict subset of RFC 8785 and has shared vectors.
- **SI-E2EE-1 (S1, draft, not frozen).**
  - Hybrid key exchange: X25519 through libsodium (ADR-0002) plus ML-KEM-768, joined by an HKDF combiner.
  - Content encryption: AES-256-GCM in 4 MiB chunks.
  - Key wrap, fingerprints and a resume commitment.
  - Deterministic test vectors and negative tests.
  - Startup crypto self-test.
- **API.**
  - Minimal APIs with source-generated JSON and an API version header.
  - Bearer authentication for service accounts.
  - `/health/live` and `/health/ready` report status only; `/health/status` is protected.
- **Frontend.** The `@secureintake/crypto-protocol` TypeScript package: an independent WebCrypto implementation of SI-JCS-1 and SI-E2EE-1 that is verified against the shared vectors.
- **Docs.**
  - ADRs 0001 to 0007.
  - Specs: S1 and SI-JCS-1.
  - `docs/BUILD-STATUS.md`.

### Fixed

- **Shutdown errors.** On shutdown, the audit checkpoint scheduler and the job dispatcher no longer log a "BackgroundService failed" critical error. The dispatcher still waits for in-flight jobs.
- **Port clash.** If the port is already in use, `serve` now prints a one-line error and exits with code 6, instead of an unhandled exception. The message points to `Http:Urls`.
- **Audit output.** `audit verify` now shows each organisation's `org_` ID instead of its internal UUID.

### Changed

- Doc 26 is now marked as superseded by S1 for byte-level details.
- Doc 28 now notes the grants that ADR-0004 blocks for service accounts.
- **CI.** Workflows also run on pushes to `build/**` branches. The TypeScript job's actions are now pinned to commit SHAs (Node 24 runtime).
- **README.** Adds a "What works today" section with an API walkthrough, and says where Development keeps its data.

### Known open items

- S1 review items R-1 to R-7 must be resolved before S1 is frozen.
- ADR-0004 blocks `platform.organisations.provision` and `org.webhooks.manage` for service accounts. A decision is needed before the M2 Swoovr exit criterion can be met.
