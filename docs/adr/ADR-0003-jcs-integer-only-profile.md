# ADR-0003: Integer-only JCS profile for signed and hashed JSON

## Status

**Accepted. Cryptographic design reviewed** (2026-11-03). The profile is approved. Implementation verification and cross-platform test evidence remain required before the dependent specs (S4, S5, S6, S8) are frozen.

## Context

F11, S4, S5, S6 and S8 canonicalise JSON before hashing or signing. Full RFC 8785 requires ECMAScript number serialisation for non-integers. Getting that byte-identical between .NET and browsers is a known source of interop bugs, and none of our signed structures need fractional numbers.

## Decision

All signed and hashed JSON uses **SI-JCS-1**, a strict subset of RFC 8785, defined normatively in [`docs/specs/SI-JCS-1-canonical-json.md`](../specs/SI-JCS-1-canonical-json.md):

- Numbers are validated **on the original token**, never via floating point. The token must match `-?(0|[1-9][0-9]*)`, must not be `-0`, and must lie in ±(2⁵³ − 1).
- **Exponent notation is rejected entirely**, even when it denotes an exact integer. Fractions are rejected likewise.
- Duplicate property names (compared after unescaping), lone surrogates, invalid or overlong UTF-8, a BOM, trailing content and nesting deeper than 64 are rejected.
- RFC 8785 ordering (UTF-16 code units), escaping and UTF-8 output are preserved exactly.

Specs cite "SI-JCS-1", never plain "JCS", so nobody assumes a general RFC 8785 library is conformant.

## Review requirements and how they're met

| # | Requirement | Evidence |
| --- | --- | --- |
| 1 | Enforce ±(2⁵³ − 1); reject fractions, out-of-range integers, duplicates and unsupported numeric forms | `Jcs.ValidateNumberToken` · vectors `fraction*`, `exponent*`, `above-safe`, `below-safe`, `huge`, `duplicate*` |
| 2 | Define exponent handling precisely | Rejected entirely (SI-JCS-1 §3.1) · vectors `exponent`, `exponent-upper`, `exponent-zero` |
| 3 | Preserve RFC 8785 ordering, escaping, Unicode and UTF-8 | Vectors `rfc8785-sort-utf16`, `escapes-*`, `surrogate-pair-*`, `sort-case-and-punctuation` |
| 4 | One profile across F11, S4, S5, S6, S8 and every other signed or hashed JSON | SI-JCS-1 "Applies to"; the audit writer and checkpoint signer use `Jcs` |
| 5 | Reject unsupported audit data before persistence; roll back without partial changes | `AuditEntry.EnsureValid()` runs in `AuditEntry.Of` and again before any audit SQL · `Audit_failure_rolls_back_the_business_change` (SQLite and PostgreSQL) |
| 6 | Cross-platform .NET and JavaScript interop: boundary integers, negative zero, exponents, duplicates, Unicode, nesting, invalid values | `docs/specs/vectors/si-jcs-1.json` (13 valid, 30 invalid) passes in .NET (`JcsVectorTests`) and in an independent TypeScript parser (`frontend/packages/crypto-protocol/test/jcs.test.ts`) |
| 7 | Work on the original numeric representation | .NET uses `JsonElement.GetRawText()` and TypeScript keeps the raw token. Neither uses `double` |

## Consequences

- Canonical bytes are trivially identical across implementations.
- Audit `data` values are limited to strings, integers in the safe range, booleans, GUIDs and null.
- The browser can't use `JSON.parse` for signed or hashed JSON: it silently drops duplicate keys and rounds numbers. The portal uses the strict parser in `crypto-protocol`.
