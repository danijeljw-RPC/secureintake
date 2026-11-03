# Architecture Decision Records

The v1.8 to v4.7 note series is closed (foundations F16). New decisions go here as `ADR-NNNN-title.md`, with one line added to [DECISIONS.md](../DECISIONS.md).

Each ADR has four sections: **Context**, **Decision**, **Consequences**, **Status** (`proposed`, `accepted`, `superseded by ADR-NNNN`). An ADR that touches a Sensitive permission, a crypto spec or a security invariant needs a second reviewer before it's `accepted`.

| ADR | Title | Status |
| --- | --- | --- |
| [0001](ADR-0001-repository-layout-and-build-rules.md) | Repository layout and build rules | proposed |
| [0002](ADR-0002-libsodium-for-x25519.md) | Call libsodium directly for X25519 | **accepted**, crypto design reviewed |
| [0003](ADR-0003-jcs-integer-only-profile.md) | Integer-only JCS profile (SI-JCS-1) | **accepted**, crypto design reviewed |
| [0004](ADR-0004-service-accounts-hold-service-roles-only.md) | Service accounts hold service roles only and never bypass JIT | **accepted** |
| [0005](ADR-0005-postgres-runtime-role-and-audit-grants.md) | PostgreSQL runtime role, RLS and append-only audit grants | proposed |
| [0006](ADR-0006-public-reference-check-symbol.md) | Public reference check symbol | proposed |
| [0007](ADR-0007-production-migration-policy.md) | Production migrations are explicit and operator-controlled | **accepted** |
