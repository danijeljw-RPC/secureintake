# SI-JCS-1: Canonical JSON Profile

| | |
| --- | --- |
| Status | **Normative draft.** Design accepted ([ADR-0003](../adr/ADR-0003-jcs-integer-only-profile.md), cryptographic design reviewed 2026-11-03). Frozen together with the first spec that depends on it |
| Applies to | Every JSON structure that is hashed or signed: audit records and checkpoints (F11, S8), recipient manifests and OKD snapshots (S4), Submission Evidence Manifests (S5), receipts (S6) and any later signed or hashed JSON |
| Implementations | .NET `src/SecureIntake.Cryptography/Canonical/Jcs.cs` · TypeScript `frontend/packages/crypto-protocol/src/jcs.ts` |
| Vectors | [`vectors/si-jcs-1.json`](vectors/si-jcs-1.json) |

## 1. Relationship to RFC 8785

SI-JCS-1 is a **strict subset** of the JSON Canonicalization Scheme (RFC 8785):

- **Same output where both accept.** For every input SI-JCS-1 accepts, its output is byte-identical to RFC 8785's.
- **Narrower input.** SI-JCS-1 rejects inputs that RFC 8785 would accept and normalise, most importantly any non-integer number.

Readers MUST NOT assume a general RFC 8785 implementation is conformant. A general implementation produces correct bytes for valid input, but it doesn't reject what this profile rejects. A signer or verifier that accepted out-of-profile input could disagree with the other side about whether a document is valid.

Specs that use this profile cite it as "SI-JCS-1", not "JCS".

## 2. Input

1. The input is a byte sequence. It MUST be well-formed UTF-8 (RFC 3629). Overlong encodings, surrogate code points encoded in UTF-8, and invalid sequences are rejected.
2. A leading byte-order mark is rejected.
3. The input MUST be exactly one JSON value (RFC 8259), optionally surrounded by JSON whitespace (space, tab, LF, CR). Trailing content, comments, trailing commas and single quotes are rejected.
4. Nesting depth MUST NOT exceed **64** (arrays and objects combined). Depth 64 is accepted; depth 65 is rejected.

## 3. Values

### 3.1 Numbers

The **original number token** is validated, never a parsed floating-point value:

- The token MUST match `-?(0|[1-9][0-9]*)`. A fraction (`1.0`, `1.5`) or an exponent (`1e3`, `1E0`) is **rejected, even when the value is an exact integer**.
- The token MUST NOT be `-0`.
- The value MUST lie in **−(2⁵³ − 1) … 2⁵³ − 1**. Range checking uses exact integer arithmetic (64-bit or arbitrary precision), never doubles.
- Output is the token unchanged. It's already the RFC 8785 serialisation, because the grammar excludes leading zeros, a plus sign and `-0`.

`NaN`, `Infinity`, leading zeros, a plus sign and bare fractions aren't valid JSON and are rejected by the grammar.

### 3.2 Strings

- After escape decoding, a string MUST be well-formed UTF-16. A lone high or low surrogate, whether escaped (`"\ud800"`) or arising otherwise, is rejected. A correctly escaped surrogate pair is accepted and output as the raw character.
- Raw control characters (U+0000 to U+001F) inside a string are rejected, as RFC 8259 requires.
- Output escaping is exactly RFC 8785 §3.2.2.2:
  - `"` → `\"` and `\` → `\\`.
  - U+0008, U+0009, U+000A, U+000C and U+000D → `\b`, `\t`, `\n`, `\f` and `\r`.
  - Other U+0000 to U+001F → `\u00xx` with **lowercase** hex.
  - Everything else, including `/`, U+007F, U+2028 and non-ASCII characters, is written literally in UTF-8.

### 3.3 Objects

- Property names are compared **after** escape decoding. A duplicate name is rejected, including one that only becomes a duplicate after decoding (`{"a":1,"a":2}`).
- Properties are sorted by the UTF-16 code units of their names, compared as unsigned 16-bit integers (RFC 8785 §3.2.3). This isn't code-point order and isn't locale-aware.

### 3.4 Literals and output

`true`, `false` and `null` are output as is. Output has no insignificant whitespace and is encoded as UTF-8 without a BOM.

## 4. Data models built on SI-JCS-1

A spec that defines a hashed or signed JSON structure MUST give every numeric field an integer type within the safe range. Common cases:

- times: Unix seconds or milliseconds
- sizes, counts and sequence numbers: integers
- anything else, such as decimals or amounts: a string with a defined format

Producers MUST validate values before persisting or signing. For audit records, an unsupported value rejects the whole audit write, and that rolls back the business transaction it belongs to (F11; tested in `AuditIntegrationTests`).

## 5. Conformance

An implementation conforms if it produces `canonicalHex` exactly for every `valid` vector, and raises an error for every `invalid` vector, in [`vectors/si-jcs-1.json`](vectors/si-jcs-1.json). The vectors cover:

- RFC 8785 sort order
- boundary integers
- negative zero
- exponent and fraction forms
- duplicates, including after unescaping
- lone surrogates
- invalid and overlong UTF-8
- a BOM
- raw control characters
- depth 64 and 65
- whitespace, trailing content, comments and empty input

The .NET and TypeScript implementations are independent and both pass all 43 vectors.
