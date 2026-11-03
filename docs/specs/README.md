# Specifications

Versioned independently of the application (doc 20 §8). Plan: [doc 26 §4](../architecture/26-cryptographic-specifications.md). Nothing here is frozen. Freezing a crypto spec needs external reviewer approval.

| Spec | Title | Status |
| --- | --- | --- |
| [S1](S1-si-e2ee-1.md) | `SI-E2EE-1` protocol | Draft, byte-level. Candidate values provisionally accepted; open review items in §12 |
| [SI-JCS-1](SI-JCS-1-canonical-json.md) | Canonical JSON profile (used by S4, S5, S6, S8) | Normative draft; design accepted (ADR-0003) |
| S2 | `.sif` file format | Not started (carries the S1 §6 header) |
| S3 | Encrypted form payload | Not started |
| S4 | Recipient manifest and OKD | Not started |
| S5 | Submission Evidence Manifest | Not started |
| S6 | `.sir.json` receipt | Not started |
| S7 | `.sik` key package | Not started (v1.0) |
| S8 | Audit chain and checkpoints | Implemented in code (`Cryptography/Audit`); text not written |
| S9 | `.siep` evidence package | v1.1 |
| S10 | Token formats | Partly in code (`sit_` service tokens, public reference check symbol, ADR-0006); text not written |

## Test vectors

| File | Produced by | Verified by |
| --- | --- | --- |
| [`vectors/si-e2ee-1.json`](vectors/si-e2ee-1.json) | `tests/SecureIntake.UnitTests/VectorGenerator.cs` (`SECUREINTAKE_WRITE_VECTORS=deterministic`, or `all` on Linux with OpenSSL 3.5+ for the hybrid section) | .NET `SiE2ee1VectorTests` · TypeScript `frontend/packages/crypto-protocol/test/si-e2ee-1.test.ts` |
| [`vectors/si-jcs-1.json`](vectors/si-jcs-1.json) | Hand-written | .NET `JcsVectorTests` · TypeScript `test/jcs.test.ts` |

Regenerating vectors changes the hybrid KEM ciphertext, because ML-KEM encapsulation uses platform randomness. Every other value is deterministic and must not change without a new suite (S1 §1).
