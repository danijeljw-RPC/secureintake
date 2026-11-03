# ADR-0002: Call libsodium directly for X25519

## Status

**Accepted. Cryptographic design reviewed** (2026-11-03). The architectural approach is approved. Implementation verification and test evidence remain required before S1 is frozen.

## Context

Doc 26 §3 says X25519 isn't in the .NET BCL and suggests "NSec over libsodium, or a vetted managed implementation". The S1 hybrid combiner needs the **raw** X25519 shared secret as HKDF input. NSec deliberately doesn't export raw shared secrets.

## Decision

Bind the two libsodium functions needed (`crypto_scalarmult_curve25519` and `crypto_scalarmult_curve25519_base`) with source-generated `LibraryImport` in `SecureIntake.Cryptography/Native/Sodium.cs`. Use the official `libsodium` NuGet package's native binaries. No other cryptographic library or abstraction is added just to avoid the native binding. Further functions (Argon2id for S7) are bound only when a spec needs them.

## Review requirements and how they're met

| # | Requirement | Implementation | Evidence |
| --- | --- | --- | --- |
| 1 | Official binaries, source-generated `LibraryImport`, AOT-compatible | `libsodium` 1.0.20.1 package · `[LibraryImport]` partial methods | AOT publish (osx-arm64) with no trim or AOT warnings. CI publishes linux-x64, linux-arm64, osx-arm64 and win-x64 |
| 2 | Check every native return code; reject invalid or low-order keys | Every call checks for `0`. libsodium returns `−1` for an all-zero result. Lengths are checked before the call | Seven rejected public keys in the S1 vectors, plus wrong-length tests (.NET and TypeScript) |
| 3 | No logging, persistence or public exposure of private keys and shared secrets; clear buffers | `X25519`, `HybridKem` and `KeyWrap` are `internal`. `HybridPrivateKey` has no export. Secrets are zeroed with `CryptographicOperations.ZeroMemory` | Public API review in S1 §10 |
| 4 | Validate native-library availability and platform support | `Sodium.AvailabilityError` catches a missing or incompatible library. `CryptoSelfTest` runs the RFC 7748 known answer and checks ML-KEM at startup. Production and Hardened refuse to start if the suite isn't usable; Development warns. `/health/status` reports `crypto.*` reasons | `CryptoSelfTest` unit test · `ConfigValidatorTests.Missing_ml_kem_blocks_production_but_only_warns_in_development` |
| 5 | RFC 7748 vectors, invalid-key tests, hybrid interoperability tests | `docs/specs/vectors/si-e2ee-1.json` | .NET: all pass on Alpine (OpenSSL 3.5), with ML-KEM skipped on macOS. TypeScript (WebCrypto X25519): RFC 7748, rejected keys, and the hybrid vector's X25519 half, combiner and unwrap |
| 6 | Minimal binding, isolated in `SecureIntake.Cryptography` | Three functions (`sodium_init` plus the two above) in one internal class | Architecture tests: `LibraryImport` and `DllImport` appear only in `Native/Sodium.cs`, only the Cryptography project references `libsodium`, and `X25519`, `HybridKem`, `KeyWrap` and `Sodium` are non-public |

The exact X25519 shared-secret handling (raw RFC 7748 output, all-zero rejection, no pre-hashing) and the combiner input encoding are in [S1 §5](../specs/S1-si-e2ee-1.md).

## Consequences

- AOT-safe, no reflection, using libsodium's reviewed implementation.
- The repository owns a small native binding, which stays in the crypto reviewer's scope.
- Native libraries ship beside the executable for each runtime identifier. A deployment missing them fails the startup self-test instead of failing at first use.
