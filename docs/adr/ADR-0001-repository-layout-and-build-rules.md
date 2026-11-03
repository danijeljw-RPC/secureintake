# ADR-0001: Repository layout and build rules

## Context

Foundations F3 and F4 fix the stack (.NET 10, Native AOT, hand-written SQL) and the module list. M0 needs a scaffold that enforces these rules mechanically rather than by review.

## Decision

- One solution, `SecureIntake.slnx`. Projects under `src/`, tests under `tests/`, generators under `tools/`.
- `Directory.Build.props` applies to every project: nullable on, warnings as errors, `latest-recommended` analysers, invariant globalisation. Every project under `src/` is marked `IsAotCompatible` and `IsTrimmable`, so a trim or AOT warning fails the build.
- `Directory.Packages.props` pins every package version centrally.
- Allowed project references (enforced by `tests/SecureIntake.ArchitectureTests`):

  | Project | May reference |
  | --- | --- |
  | Domain, Contracts, Cryptography | nothing in SecureIntake |
  | Application | Domain, Contracts |
  | Infrastructure | Application, Cryptography |
  | Api | Application |
  | Worker | Infrastructure |
  | Host | Api, Worker, Infrastructure |

- SQL appears only in `SecureIntake.Infrastructure`. EF Core, Dapper, Newtonsoft.Json and AutoMapper are banned by test.
- `permissions.json` is generated from doc 28 by `tools/gen_permissions.py`. CI fails if it drifts.
- CI (`.github/workflows/ci.yml`) runs tests on Linux, macOS and Windows; the isolation suite on PostgreSQL with a non-owner role; the hybrid-KEM tests on Alpine (OpenSSL 3.5); and an AOT publish plus smoke test for linux-x64, linux-arm64, osx-arm64 and win-x64.

## Consequences

- Module boundaries can't erode silently. Adding a reference means editing the architecture test, which is a visible change in review.
- The ML-KEM tests skip on macOS and on Linux with OpenSSL before 3.5. The dedicated Alpine CI job fails if they skip there.

## Status

Proposed, 2026-11-03.
