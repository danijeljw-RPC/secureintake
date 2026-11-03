# Resumable Uploads and Encrypted Drafts

Source: notes v3.2. Resolved questions: [QUESTIONS.md](../QUESTIONS.md) (Q-UPL-*). Planning decisions applied below take precedence over the source notes.

> **Planning decisions applied (2026-11-03)** — these override the text below where they differ. See [QUESTIONS.md](../QUESTIONS.md).
> - Upload session and draft are **one entity**, with endpoints under `/api/public/upload-sessions/{sessionId}/…` (Q-UPL-1).
> - Defaults are confirmed: 4 MiB chunks, 24 hours initial, 7 days max, 10-minute presigned URLs (Q-UPL-2, [27](27-production-defaults.md)).
> - Same-browser resume in v1.0. Cross-device draft recovery in v1.2. Nonce construction: [26](26-cryptographic-specifications.md) §2.

## Decision

Uploaders can **resume interrupted submissions within a configurable, bounded period**. The server stores only **encrypted** file content and **encrypted** confidential form values — **never plaintext drafts**.

Implemented with durable server-side upload sessions, resumable ciphertext chunks, and optional browser-local encrypted draft state. Two distinct operations:

- **Resume upload** — continue transferring ciphertext the browser already encrypted.
- **Resume draft** — reopen an unfinished form, edit fields or add files, then complete.

The encryption protocol, recipient snapshots and access-grant policies remain authoritative.

## 1. Architecture

```text
Uploader starts submission (access grant · identity checks · pinned form and recipient policies)
  → Durable draft session (expiry · quota reservation · upload manifest · resume credential)
       ├── Browser: local encrypted form state · file references · encryption context
       └── Server/storage: encrypted chunks · upload progress · reservations
  → Finalisation (verify ciphertext · commit submission · issue receipt · release unused reservations)
```

## 2. Draft modes

| Mode | Server stores | Browser stores | Capability |
| --- | --- | --- | --- |
| Upload-only recovery | Ciphertext, manifest, progress | Resume credentials, local file references | Continue interrupted upload |
| Encrypted form draft (optional per organisation/dropbox) | Ciphertext, manifest, **encrypted** form state | Draft-unlock material, optional local encrypted state | Reopen unfinished form |

**Browser limitation:** after restart a browser can't reopen an arbitrary local file without user action or a persisted file handle. Recovery works without the original file **if the complete ciphertext already reached storage**; for partially uploaded files the uploader may need to reselect the file.

## 3. Lifecycle

```text
Created → Preparing → Uploading ⇄ Paused → Ready → Finalising → Completed
also: Cancelled · Expired · Failed-cleanup
```

Transactional, idempotent transitions. A `Finalising` session is never finalised twice on client retry.

## 4. Session persistence (tenant data plane)

```text
upload_draft_sessions(id, organisation_id, dropbox_id, access_grant_id, uploader_identity_id,
  form_snapshot_id, recipient_snapshot_id, status, created_at, last_activity_at,
  expires_at, maximum_expires_at, version)
```

Pinned to the form and recipient snapshots at start; template/policy updates never mutate a draft. Security-sensitive changes (e.g. key revocation) are revalidated before finalisation.

## 5. Resumable ciphertext

Fixed-size chunks, each independently authenticated; the browser can reproduce a specific encrypted chunk without re-encrypting the whole file. Requires deterministic chunk identification and **strict nonce uniqueness** under a reviewed construction.

```text
File selected → create file record → encrypt chunk locally → upload chunk
→ server confirms durable receipt → repeat → verify manifest → mark file ready
```

The server tracks **confirmed** chunks, not bytes the client claims to have sent.

### Chunk manifest

```json
{ "fileId": "file_123", "protocolVersion": 1, "chunkSize": 4194304, "chunkCount": 25,
  "ciphertextLength": 104861600,
  "chunks": [ { "index": 0, "ciphertextLength": 4194320, "digest": "sha256:..." } ] }
```

Authenticated and bound to the encrypted file. The server verifies digests and sizes without decrypting; the client verifies authenticated chunk contents during decryption (a ciphertext hash alone isn't plaintext authenticity).

## 6. Resume protocol

```text
GET /api/public/upload-sessions/{sessionId}/status
```

```json
{ "sessionId": "upl_123", "status": "uploading", "expiresAt": "2026-11-04T12:00:00Z",
  "files": [ { "fileId": "file_456", "confirmedChunks": [0, 1, 2, 3, 4], "totalChunks": 25 } ] }
```

Authorised by the scoped resume capability (+ identity binding where required). The client uploads only missing chunks. Large files use compact ranges or pagination rather than huge arrays.

## 7. Resume credentials

Separate capability from the upload grant: opaque, high-entropy, bound to one draft session, one organisation/dropbox, the original uploader identity where required, bounded expiry and permitted draft operations. Never grants access to other submissions, reviewer ciphertext or decryption.

- Same-browser recovery: scoped secure cookie.
- Cross-browser recovery: separate resume link with stronger identity verification, using fragment-based secret handling ([12](12-upload-links-and-invitations.md)).
- **No long-lived resume tokens in `localStorage`.**

## 8. Browser-local draft state (IndexedDB, optional)

May store: `draft_id, form_snapshot_id, encrypted_form_state, file_metadata, file_handle_reference, upload_manifest, confirmed_chunk_cache, last_saved_at`. No plaintext sensitive values.

A separate **draft-state encryption key** (not the file DEK), protected by a user-controlled unlock or protected browser-local key. Cross-device recovery may need an encrypted recovery package or passphrase-derived key. Browser storage can vanish — the UI never promises permanence.

## 9. Cross-device recovery

- **Same device:** restore local encrypted state; resume with existing session credential.
- **Different device:** open resume link → complete identity verification → fetch server-stored encrypted draft. Editing E2EE fields requires the draft decryption secret; **a resume token alone never yields plaintext**.
- Partially uploaded files without the original or remaining ciphertext must be re-provided; missing plaintext can't be reconstructed.

**Ship same-browser recovery before cross-device draft editing.**

## 10. Expiration

| Setting | Illustrative default |
| --- | --- |
| Initial draft lifetime | 24 hours |
| Maximum lifetime | 7 days |
| Inactivity timeout | 24 hours |
| Upload URL lifetime | 10 minutes |
| Resume capability lifetime | ≤ draft lifetime |
| Cleanup grace period | 1 hour |

Configurable within deployment hard limits. **Sliding expiry** up to the absolute maximum (created 3 Nov, initial expiry 4 Nov, max 10 Nov — activity extends but never past 10 Nov). Server clock is authoritative. Revocation or organisation suspension ends drafts early.

## 11. Cleanup

Durable, idempotent, via the DB-backed workers:

```text
Draft expired → mark expiration pending → fence new uploads and finalisation
→ enumerate temporary ciphertext → delete from storage → verify deletion or record provider limitation
→ release reserved quota → mark cleanup complete
```

Retries with backoff. Expired drafts are inaccessible to the uploader even if physical cleanup lags. For BYO storage (revoked credentials, provider down) record unresolved cleanup obligations — **never falsely report deletion**.

## 12. Quota reservations

| Resource | Reserved |
| --- | --- |
| Active draft sessions | At creation |
| Expected upload bytes | Before upload |
| Confirmed temporary ciphertext | During upload |
| Object count | Per file/chunk |
| Concurrent upload slots | While active |
| Completed submissions | At finalisation |

Temporary ciphertext counts toward quota (else abandoned drafts exhaust storage). Atomic changes. Removing a file doesn't release quota until its temporary objects are removed/reconciled. See [04](04-resource-governance-and-quotas.md).

```json
{ "draftId": "upl_123", "reservedBytes": 104857600, "confirmedTemporaryBytes": 62914560,
  "reservedObjects": 25, "expiresAt": "2026-11-04T12:00:00Z" }
```

## 13. Uploader UX

Per-file progress and recovery info, with distinct states: **Encrypting · Uploading · Waiting for connection · Paused · Verifying · Ready to submit · Completed**. Show expiry ("Your unfinished submission expires on …"). Never show 100% just because bytes were transferred — finalisation and verification are separate steps.

## 14. Offline behaviour

Interrupted connectivity is supported; full offline isn't. The browser may keep preparing encrypted chunks offline but can't confirm receipt or finalise. No service worker in v1 ([07](07-upload-portal-distribution.md)); no background uploads after the browser closes.

## 15. Encryption and resume safety

The protocol must guarantee: unique nonce per encryption under a key; stable chunk identities; authenticated chunk ordering; detection of missing/duplicated chunks and truncation; authenticated file metadata; safe recovery after interrupted encryption; no reuse of encryption state across files.

- **No nonces from mutable counters** that can reset after a crash. Use deterministic derivation from a unique file context + immutable chunk index (or an equivalent reviewed construction).
- If the source file changes between sessions, create a new file version and encryption context — never continue under the old one.

## 16. API

```text
POST /api/public/upload-access-sessions/{id}/upload-sessions
GET  /api/public/upload-sessions/{sessionId}
GET  /api/public/upload-sessions/{sessionId}/status
POST /api/public/upload-sessions/{sessionId}/resume
POST /api/public/upload-sessions/{sessionId}/pause
POST /api/public/upload-sessions/{sessionId}/cancel
POST /api/public/upload-sessions/{sessionId}/files
GET  /api/public/upload-sessions/{sessionId}/files/{fileId}/chunks
POST /api/public/upload-sessions/{sessionId}/files/{fileId}/chunks
POST /api/public/upload-sessions/{sessionId}/finalise
```

All require scoped access-session authorisation; never accept client object keys. Upload sessions and drafts are one entity, the **Upload Session** (`upl_…`), and these endpoints are its single family (Q-UPL-1, P-14). Tables are renamed `upload_draft_*` → `upload_session_*` at implementation.

## 17. Data model

```text
upload_draft_sessions            upload_draft_identity_bindings
upload_draft_session_versions    upload_draft_quota_reservations
upload_draft_files               upload_draft_cleanup_operations
upload_draft_file_manifests      upload_draft_cleanup_attempts
upload_draft_chunks              upload_draft_events
upload_draft_resume_credentials
```

Tenant-scoped; cleanup jobs work with shared and dedicated data planes.

## 18. Metrics

```text
secureintake_upload_drafts_active | expired | resumed | completed
secureintake_upload_bytes_temporary | confirmed
secureintake_upload_cleanup_pending | failed
secureintake_upload_resume_failures
secureintake_upload_finalisation_duration
```

No uploader-identifying labels. Dashboards show temporary storage and cleanup backlog.

## 19. Implementation order

1. Durable draft state machine.
2. Chunked ciphertext upload protocol.
3. Server-confirmed chunk tracking.
4. Idempotent finalisation.
5. Scoped resume capabilities.
6. Same-browser recovery.
7. Expiry and cleanup.
8. Quota reservation integration.
9. Encrypted form draft persistence.
10. Cross-browser recovery.
