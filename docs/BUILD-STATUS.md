# Build Status

Last updated 2026-11-03 (second pass: your review decisions applied). Nothing is committed yet; everything is in the working tree.

## This pass: your decisions, and what changed

| Your decision | Done | Where |
| --- | --- | --- |
| **ADR-0004 accepted** with clarifications | Service accounts are denied every permission above Standard, whatever is stored. Same rules on the CLI and the API (one store, no CLI-only logic). Privileged CLI verbs need the OS account in `Cli:AuthorisedOperators` outside Development, and are audited as `host_cli.command` with `user@host`. `/health/status` stays protected; `/health/ready` is status-only | [ADR-0004](adr/ADR-0004-service-accounts-hold-service-roles-only.md) · `Authorizer.EvaluateServiceAccount` · `HostOperator.cs` |
| **KEM combiner**: keep HKDF, define it fully | S1 §5.4 gives exact inputs, order, lengths, labels, output length and rationale, with review items R-1 to R-3. Any later change is a new suite (S1 §1) | [S1](specs/S1-si-e2ee-1.md) |
| **Byte-level S1** with vectors and negative tests | See "S1 changes" below | [S1](specs/S1-si-e2ee-1.md) · [vectors](specs/vectors/si-e2ee-1.json) |
| **ADR-0002 accepted** (crypto design reviewed) | The binding is internal and checks every return code. Startup self-test: library present, RFC 7748 known answer, ML-KEM available (Production refuses to start otherwise). Architecture tests keep native interop and raw primitives confined | [ADR-0002](adr/ADR-0002-libsodium-for-x25519.md) (requirement → evidence table) |
| **ADR-0003 accepted** (crypto design reviewed) | Normative [SI-JCS-1](specs/SI-JCS-1-canonical-json.md). Numbers are checked on the original token; exponents, fractions and `-0` are rejected. Audit data is validated before any SQL. Shared .NET/TypeScript vectors | [ADR-0003](adr/ADR-0003-jcs-integer-only-profile.md) |
| **Production migrations manual** | Lock (PostgreSQL advisory lock or SQLite lock file) · run log with outcome · audit record per migration · all-or-nothing transactions · `migrate status` · interrupted-run detection · separate owner connection required · runtime-role privilege check · readiness reason codes | [ADR-0007](adr/ADR-0007-production-migration-policy.md) |

### S1 changes from the doc 26 candidates

These are needed to make the encoding unambiguous; S1 §13 has the list.

- **Key schedule.** The DEK is no longer the AES key directly. Distinct HKDF labels derive `contentKey`, `nonceBase` and `commitKey`.
- **`manifestDigest`.** Now SHA-256 over a fixed 38-byte header of pre-encryption values only (suite, fileId, chunk size, length, chunk count). Envelopes and ciphertext stay out of it, which removes the circular dependency.
- **Fixed-length binary IDs.**
  - `recipientKeyId` is the 16-byte UUID.
  - `suiteId` in binary is the `u16be` code `0x0001`.
  - The wrap AAD now also binds the recipient fingerprint, so the ML-KEM half of the key is covered.
- **Fingerprint.** SHA-256 over a label, `0x00`, the suite code and the fixed-length keys.
- **Resume commitment.** S1 §7.4 adds a local keyed resume commitment. Without it, a changed source file on resume would reuse an AES-GCM nonce with different plaintext.
- **Wrap nonce.** Drawn from the CSPRNG, and each KEK is single-use by construction. Key wrap is reachable only through `Envelope.Seal`.

## Verification (run on 2026-11-03)

| Suite | Result |
| --- | --- |
| .NET unit tests, Alpine Linux (OpenSSL 3.5) | **137 / 137**, none skipped, including hybrid ML-KEM vectors and negative tests |
| .NET unit tests, macOS | 133 passed, 4 skipped (ML-KEM unavailable on macOS) |
| Integration, SQLite plus PostgreSQL 17 (Docker, non-owner runtime role) | **59 / 59**. New: migration lock refusal on both databases, failed migration fully rolled back and logged, interrupted-run detection, newer-than-build and checksum-mismatch states, runtime role refused `CREATE TABLE` (42501), CLI operator allowlist, CLI audit records, CLI applying the same service-account rules |
| Architecture tests | **18 / 18** (new: native interop confined; raw primitives non-public) |
| TypeScript `crypto-protocol` (Node 26, WebCrypto, independent implementation) | **55 / 55**: all SI-E2EE-1 deterministic vectors, the hybrid vector's X25519 half, combiner and unwrap, 43 SI-JCS-1 vectors, and negative tests |
| Native AOT publish (osx-arm64) | Clean, with no warnings. `migrate status`, `migrate`, operator auditing and the service-account refusal checked on the native binary |
| CI workflow | **Still not run** (no GitHub remote). New `portal-crypto` job; the Alpine job now fails if any test is skipped |

## ⚑ Needs your decision

1. **Two doc 28 grants are now blocked for service accounts** (ADR-0004, Consequences):
   - `platform.organisations.provision` means the Provisioning Client can't create organisations over the API.
   - `org.webhooks.manage` means an API client can't register webhooks. This blocks the **M2 Swoovr exit criterion**.

   My recommendation, not implemented: add narrow Standard permissions for the automated paths and keep the human ones Elevated:
   - `platform.organisations.request`: creates an organisation in pending approval only
   - `org.webhooks.manage_own`: only the caller's own endpoints

   That needs a catalogue ADR.
2. **S1 review items R-1 to R-7** (S1 §12) need the external reviewer. Most notable:
   - R-1: whether the combiner should also bind the ML-KEM ciphertext and key
   - R-5: whether the resume rule is sufficient
   - R-6: whether file length disclosure is acceptable for v1

## Still to do (M0 and M1)

| Item | Note |
| --- | --- |
| Spec texts S2 to S6, S8 and S10 | S1 and SI-JCS-1 are written. S8 and parts of S10 exist only as code |
| ML-KEM in TypeScript | `@noble/post-quantum`, pinned, in M2. Until then the TypeScript side can't decapsulate the hybrid vector |
| Threat model, `configuration.schema.json`, security policy catalogue | M0 |
| Built-in identity (passkeys, password+TOTP), OIDC, sessions, CSRF | M1. After this, normal platform administration moves off the CLI (ADR-0004 §6) |
| DRR fair scheduler, outbox dispatcher, 1,000-record checkpoint trigger, offline audit verifier, OpenAPI | M1 |
| Doc 20 upgrade runbook | Must describe the two-step `migrate` then roll-out procedure (ADR-0007) |

## Earlier pass (still true)

Scaffold, domain, permission catalogue, audit chain and checkpoints, SQLite and PostgreSQL with RLS, tenancy and fencing, jobs, provisioning, service-account tokens, API, host CLI and CI are as described in the first pass. ADR-0001 and ADR-0006 are still `proposed`.
