# Reviewer Clients and Decryption

Source: notes v2.5. Key portability and recipient models continue in [09](09-key-management-and-recipients.md). Resolved questions: [QUESTIONS.md](../QUESTIONS.md) (Q-REV-*). Planning decisions applied below take precedence over the source notes.

> **Planning decisions applied (2026-11-03)** — these override the text below where they differ. See [QUESTIONS.md](../QUESTIONS.md).
> - Client binary: **`secureintake-client`** (Q-GEN-4). Endpoints consolidated under `/recipient-keys` (Q-REV-2).
> - Browser preview allowlist in v1.0: PNG, JPEG, static GIF, WebP and plain text. PDF (sandboxed PDF.js) in v1.1. Never SVG, HTML or Office (Q-REV-3).
> - Desktop GUI: **Avalonia**, v1.3. File format spec: S2 in [26](26-cryptographic-specifications.md).

## Decision

Support **both browser-based reviewer decryption and independently distributed desktop/CLI clients**. All clients share the same encrypted file format, recipient envelopes, authentication rules and API contracts.

The difference is **trust model, not permissions**: the browser is convenient for everyday review; an independently installed client resists a compromised web frontend (provided its own distribution and updates are trusted).

> The API authorises access to encrypted material. The client independently determines whether it holds the right private key. **Administrative access does not imply decryption access.**

## 1. Architecture

```text
Secure Intake API (identity · RBAC · file metadata · ciphertext · envelopes · audit)
   ├── Browser reviewer (optional admin frontend; browser-held private key)
   └── Desktop / CLI (independently installed; OS keystore, encrypted key file or hardware-backed key)
Common cryptographic protocol (identical ciphertext · manifest validation · recipient envelopes)
```

## 2. Authentication

| Client | Authentication |
| --- | --- |
| Browser | Secure session cookie, CSRF protection, MFA |
| Desktop | OIDC Authorization Code + PKCE via system browser |
| Interactive CLI | OIDC Auth Code + PKCE; device flow only where supported and authorised |
| Automation CLI | Scoped machine identity (OAuth2 client credentials or equivalent) |
| Offline decryption | No live login needed if the private key is available (intentional) |

Once a reviewer has ciphertext and the key, the platform can't cryptographically revoke that copy.

Ciphertext retrieval requires: authenticated identity; active organisation membership; resource-level permission; current organisation and file access policy; required recent MFA/step-up; tenant routing and storage authorisation. **Possession of a public key is never proof of authorisation.**

## 3. Private-key custody

| Mode | Storage | Use |
| --- | --- | --- |
| Browser-managed | Browser-local encrypted key material | Convenient web review |
| Desktop-managed | Encrypted key file or OS-protected storage | Normal desktop review |
| Organisation-managed | External KMS/HSM or controlled key service | Enterprise and automation |

All follow the same public-key registration and envelope protocol.

**Browser:** generate locally or import an approved encrypted key package. Use non-extractable WebCrypto keys where the chosen hybrid/PQ suite supports it — **verify before promising**. Otherwise use an encrypted key container with explicit unlock. Never claim IndexedDB ≈ HSM.

**Desktop:** encrypted portable key files; Windows DPAPI/CNG, macOS Keychain, Linux Secret Service where compatible; PKCS#11/vendor KMS as optional advanced options. Hardware-backed custody is evaluated per algorithm — not every keystore/HSM supports the chosen PQ algorithms.

## 4. Key enrolment

1. Authenticate (with required MFA). 2. Generate or import key (private key stays with reviewer/organisation). 3. Register public key with suite and **proof of possession**. 4. Approve per organisation key-registration policy. 5. Activate as recipient for new submissions. 6. Audit (fingerprint, approval, policy version).

Proof of possession is defined per suite — for encryption-only keys a signing challenge may not fit; use a reviewed challenge encapsulation/decryption exchange. **Unverified public keys are never activated.**

## 5. Ciphertext retrieval

```text
GET /api/organisations/{orgId}/files/{fileId}
```

```json
{
  "fileId": "file_123", "submissionId": "sub_456",
  "encryption": { "protocolVersion": 1, "suiteId": "approved-suite-id", "manifestDigest": "base64url-encoded-digest" },
  "ciphertext": { "length": 10485760, "downloadSessionRequired": true },
  "recipientEnvelopes": [ { "recipientKeyId": "key_789", "envelopeId": "env_123" } ]
}
```

```text
POST /api/organisations/{orgId}/files/{fileId}/download-sessions
```

Returns a short-lived scoped object-storage URL **or** an authenticated API streaming endpoint. Never exposed to public uploaders.

Safeguards: short expiry; exact object scope; no arbitrary object keys; authorisation at session creation; range requests only where compatible with authenticated chunk verification; bounded concurrent downloads; usage/audit accounting; revocation where platform-controlled. Issued presigned URLs survive permission changes until expiry — use short lifetimes, and proxy streaming where tighter revocation is needed.

## 6. Browser decryption UX

- Metadata is viewable without unlocking keys. Decryption is **explicitly initiated** — never auto-decrypt on opening a submission.
- UI states the key is used locally and not sent to the server.

| Action | Meaning |
| --- | --- |
| Decrypt and preview | Constrained local preview for a **restricted** set of types |
| Decrypt and save | Write plaintext to a reviewer-chosen destination |
| Retrieve ciphertext | Download the encrypted package without decrypting |

PDFs, images and office docs can carry malicious content or parser exploits — start with a small preview allowlist; everything else is decrypted download.

## 7. Plaintext handling

Browser: never upload plaintext to the API; no persistent browser storage; bounded memory; clear buffers where practical; revoke temporary object URLs; no caching of decrypted responses; no plaintext names/content to analytics or logs. JS can't guarantee memory erasure.

Desktop: write only to explicitly chosen locations; avoid/manage temp files carefully; warn that downloaded plaintext is outside the platform's cryptographic control.

## 8. Desktop/CLI client

**.NET 10**, sharing protocol models and interoperability tests with the backend. **CLI first** (independently distributable reference client); GUI later reusing the same services. GUI framework to evaluate: Avalonia or another cross-platform .NET option (macOS/Windows/Linux, packaging, accessibility, AOT).

```text
src/SecureIntake.Client.Core/      SecureIntake.Client.Storage/
    SecureIntake.Client.Crypto/    SecureIntake.Client.Cli/
    SecureIntake.Client.Auth/      SecureIntake.Client.Desktop/
```

The client depends only on published API contracts — never server DB models or infrastructure.

### CLI

```bash
secureintake-client auth login --server https://intake.example.com
secureintake-client keys list
secureintake-client submissions list --organisation org_123
secureintake-client files download --file file_456 --output ./document.sif
secureintake-client files decrypt --input ./document.sif --output ./document.pdf

secureintake-client keys generate --suite approved-suite-id
secureintake-client keys register --organisation org_123
secureintake-client keys export-public --output reviewer-public.json
secureintake-client keys backup --output reviewer-key-backup.sik
```

`.sif` (encrypted file) and `.sik` (key backup) are proposed formats; specify formally before implementing. Key backup requires explicit confirmation and encryption; private keys are never printed to the terminal.

### Token custody

Separate from encryption keys. Refresh tokens in OS-protected credential stores; short-lived access tokens. Headless automation uses dedicated narrow-scoped machine credentials — never a human's interactive token. A service principal may retrieve ciphertext; its ability to decrypt depends on its separately controlled private key.

## 9. Encrypted file format

One formal format for browser, desktop and automation clients:

```text
Secure Intake Encrypted File
├── Format header
├── Protocol version
├── Encryption suite identifier
├── File identifier
├── Authenticated manifest
├── Recipient key envelopes
└── Encrypted chunks
```

Deterministic/canonical encoding; explicit length bounds; versioning; all security-relevant metadata authenticated or bound to content.

| Encrypting | Decrypting | Required |
| --- | --- | --- |
| Browser | Browser, Desktop, CLI, Automation | Yes |
| Desktop | Browser | If desktop encryption is supported |
| CLI | Desktop | If CLI encryption is supported |

v1 only needs public upload encryption + authorised reviewer decryption; client-side encryption elsewhere can come later within the inbound-only boundary.

### Test vectors

Single recipient; multiple recipients; organisation recovery recipient; empty, small, multi-chunk and large files; corrupted chunk; modified manifest; invalid envelope; unsupported suite; duplicate nonce detection; truncated ciphertext; wrong private key; key rotation and historical envelopes. Every client passes the same vectors; browser↔CLI interop tests run in CI.

## 10. Revocation

- **Platform access revocation:** API stops authorising future retrieval.
- **Cryptographic key revocation:** key no longer used for new encryption; platform may restrict future envelope access.

Neither revokes already-downloaded material. Rewrapping a file key after private-key compromise doesn't restore confidentiality — identify affected files and support full re-encryption via an authorised client ([24](24-key-governance.md)).

## 11. Organisation-managed automation

Automation clients are **cryptographic recipients, not server internals**: authenticate with a scoped machine identity → retrieve authorised ciphertext → obtain private key from organisation custody → decrypt outside Secure Intake → process in the organisation's environment. Results come back only via an explicit separate integration (inbound-only preserved).

## 12. Audit events

```text
reviewer.key.generated | registered | activated | revoked
reviewer.ciphertext_access.authorised | denied
reviewer.download_session.created | expired
reviewer.key_recovery.requested | approved
```

The server can't know whether a client decrypted successfully; optional client reports are **client assertions** and the audit UI must say so.

## 13. API

```text
GET  /api/organisations/{id}/recipient-keys
POST /api/organisations/{id}/recipient-keys
POST /api/organisations/{id}/recipient-keys/{keyId}/activate
POST /api/organisations/{id}/recipient-keys/{keyId}/revoke
GET  /api/organisations/{id}/submissions
GET  /api/organisations/{id}/files/{fileId}
GET  /api/organisations/{id}/files/{fileId}/envelopes
POST /api/organisations/{id}/files/{fileId}/download-sessions
GET  /api/organisations/{id}/reviewer-access-policies
PUT  /api/organisations/{id}/reviewer-access-policies
```

Key registration/revocation require appropriate permissions and step-up authentication. Endpoints are consolidated under `/recipient-keys` for all recipient types (Q-REV-2, P-15).

## 14. Implementation order

1. Formal encrypted file format.
2. Cryptographic test vectors.
3. Shared API contracts.
4. CLI authentication and ciphertext retrieval.
5. CLI decryption.
6. Reviewer key generation and registration.
7. Browser key custody and decryption.
8. Browser preview and decrypted download.
9. Desktop GUI.
10. Organisation-managed KMS/HSM integrations.

This yields an independently distributed reference client before relying on browser decryption.
