# Upload Links, Invitations and Access Grants

Source: notes v3.1. Resolved questions: [QUESTIONS.md](../QUESTIONS.md) (Q-LNK-*). Planning decisions applied below take precedence over the source notes.

> **Planning decisions applied (2026-11-03)** — these override the text below where they differ. See [QUESTIONS.md](../QUESTIONS.md).
> - Routes: `/u/{grantId}#siu_…[&okd=…]` on every host (Q-LNK-1).
> - Invitation expiry: 14 days by default, 90 days maximum. Grace on ordinary expiry is 1 hour. Abuse signals are kept for 30 days raw and 90 days aggregated (Q-LNK-2).
> - `api_issued` grants are **v1.0**. They're the main integration path for API consumers such as Swoovr.

## Decision

Each dropbox supports **reusable shared upload links** and **individually issued invitations**, with independently configurable access policies — implemented as one unified **Upload Access Grant** system (same authentication, authorisation, quota, audit and upload-session infrastructure; not two security models).

> A link grants only the minimum authority needed to initiate a controlled inbound submission. It never grants access to reviewer data, historical submissions or decrypted content.

Upload access, **return access** and **reviewer ciphertext retrieval** are separate capabilities.

## 1. Architecture

```text
Dropbox (form · encryption recipients · upload limits · access policy)
   ├── Shared link (reusable · public or restricted · many uploaders)
   └── Invitation (individual · scoped · expiring · optional identity binding)
        ↓
Upload Access Grant evaluation (token · identity · challenges · expiry · usage · rate limits)
        ↓
Short-lived upload session (pinned form snapshot · recipient snapshot · reserved quota · ciphertext upload)
```

## 2. Grant types

| Type | Purpose | Typical constraints |
| --- | --- | --- |
| `shared_link` | Reusable public intake | Expiry, rate limits, submission quotas |
| `invitation` | Named/individually scoped intake | Identity binding, expiry, use count |
| `api_issued` | Integration-created upload capability | Scoped service identity, TTL, quotas |
| `internal_test` | Synthetic testing | Development/test environments only |

Every grant belongs to exactly one organisation and one dropbox.

## 3. Shared links

A shared link is an independently managed grant, **not** the dropbox's public ID. A dropbox can have several (website, customer portal, campaign, partner), each with its own restrictions and revocation.

```json
{ "name": "Website document upload", "type": "shared_link", "dropboxId": "dbx_123",
  "identityMode": "anonymous", "expiresAt": null,
  "limits": { "maxCompletedSubmissions": null, "maxFilesPerSubmission": 10, "maxBytesPerSubmission": 104857600 },
  "challenges": { "pinRequired": false } }
```

Limits are bounded by organisation/deployment limits ([04](04-resource-governance-and-quotas.md)). Shared links may be anonymous or require verified email, one-time code or OIDC before a session is issued.

## 4. Invitations

| Property | Behaviour |
| --- | --- |
| Recipient | Email address or external identity |
| Identity binding | Optional or required |
| Expiry | **Required** |
| Max completed submissions | Configurable |
| Max concurrent sessions | Configurable |
| Dropbox | Exactly one |
| Form version | Current at redemption, or explicitly pinned |
| Recipient encryption policy | Dropbox policy — never caller-controlled |
| Revocation | Immediate for future authorisation |
| Delivery | Email, API response, or admin-managed distribution |

No permanent uploader account required.

```json
{ "dropboxId": "dbx_123", "recipient": { "type": "email", "value": "customer@example.com" },
  "identity": { "mode": "verified_email", "required": true },
  "expiresAt": "2026-11-10T12:00:00Z", "maxCompletedSubmissions": 1, "maxConcurrentSessions": 1 }
```

Email-bound invitations require proof of control of that email — a forwarded URL alone is not enough.

## 5. Token design

Opaque, high-entropy, server-verifiable bearer tokens — **not JWTs in links** (simpler revocation; no claims exposed in URLs).

```text
siu_<public-grant-id>.<random-secret>
```

- The grant ID is a non-sensitive lookup handle; the secret has ≥256 bits of CSPRNG randomness.
- Stored only as a **keyed digest** (HMAC with a server-held lookup key via the secret-resolution infrastructure). Argon2id remains for human-chosen PINs/passwords.
- Constant-time comparison; the secret is returned only once at creation.

```csharp
public static class UploadTokenGenerator
{
    public static string GenerateSecret()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static byte[] ComputeDigest(ReadOnlySpan<byte> lookupKey, string secret)
        => HMACSHA256.HashData(lookupKey, System.Text.Encoding.UTF8.GetBytes(secret));
}
```

## 6. URL structure

```text
https://intake.example.com/u/grt_01JD…#siu_...
https://documents.example.org/u/grt_01JD…#siu_...&okd=<fingerprint>
```

Route scheme (Q-LNK-1, P-13): `/u/{grantId}` on every host. The optional `okd` fragment parameter pins the Organisation Key Directory fingerprint ([26](26-cryptographic-specifications.md) §5).

- Token in the **URL fragment** (not sent in HTTP requests). Portal JS reads it, redeems via a secure API call, then strips it with `history.replaceState`.
- Still exposed via history, malicious extensions, copied links or compromised frontend JS — so **no third-party scripts or analytics** on the portal.
- The route identifier is not the secret.

## 7. Redemption

```text
POST /api/public/upload-grants/redeem
{ "grantId": "grt_123", "secret": "<opaque-token-secret>" }
```

Returns a limited **access-session descriptor**, not a storage session:

```text
Token presented → valid? → identity required? → challenge required? → access authorised → upload session created
```

After redemption the secret isn't re-sent; use a short-lived scoped session credential or secure cookie.

## 8. Identity binding

| Mode | Verification |
| --- | --- |
| Anonymous | None |
| Verified email | Email challenge |
| One-time code | Possession of a separately issued code |
| OIDC | Validated issuer and subject |

A PIN is **not** identity — an anonymous uploader with the right PIN is still anonymous. OIDC binding matches `issuer + subject`, never email alone:

```json
{ "identityBinding": { "type": "oidc", "issuer": "https://identity.example.com", "subject": "user-12345" } }
```

Verification is scoped to the grant and its lifetime. See [14](14-uploader-identity.md).

## 9. Invitation delivery

Via the notification subsystem ([05](05-notifications.md)) and verified sender identity. Contents: organisation name, upload purpose, expiry, secure link, support info — no keys, plaintext or unnecessary detail. Provider acceptance ≠ received/opened.

## 10. Expiration

| Resource | Expiration |
| --- | --- |
| Shared link | Optional |
| Invitation | Required |
| Redemption challenge | Short-lived |
| Access session | Short-lived |
| Upload session | Configurable, bounded |
| Storage upload URL | Very short-lived |
| Return-access token | Separate policy |

Enforced server-side; client countdowns are informational. Semantics: grant expiry → no new redemptions/sessions; access session expiry → re-authenticate/redeem; upload session expiry → can't continue; revocation → future authorisation fails.

**Production default:** immediate termination on explicit **revocation**; bounded completion (short grace) on ordinary **expiry**. Configurable per organisation.

## 11. Usage limits

| Limit | Scope |
| --- | --- |
| Max completed submissions | Grant |
| Max active upload sessions | Grant |
| Max total uploaded bytes | Grant |
| Max files / bytes per submission | Dropbox / grant |
| Max redemptions | Invitation |
| Max attempts | Identity challenge |
| Rate limits | IP, grant, organisation, deployment |

Transactional reservation: reserve at session creation; consume the completed-submission allowance at finalisation; release on expiry/cancel only after cleanup reconciles (PostgreSQL row locks/atomic updates; SQLite atomic write transactions).

### One-time invitations

"One-time" = **one successfully completed submission**, not one request or redemption. States: `issued → redeemed → upload_in_progress → consumed`, plus `expired`, `revoked`. Interrupted uploads don't consume it (unless policy says so); failed attempts are bounded. Only one active reserved slot at a time; a second concurrent session request fails or securely resumes the existing one.

## 12. Revocation

Admin UI, API and CLI: one shared link; one invitation; all grants for a dropbox; all grants issued by a compromised API client; suspend a dropbox; suspend organisation public access.

Revocation blocks future redemption and access-session authorisation; active sessions are cancelled/fenced per policy. Issued presigned URLs may still work until expiry, so the server **rejects finalisation of revoked sessions** and cleans up orphaned ciphertext.

## 13. Abuse protection

| Control | Purpose |
| --- | --- |
| IP/network rate limits | Automated abuse |
| Grant-level rate limits | Protect individual links |
| Challenge attempt limits | OTP/PIN guessing |
| Max upload sizes | Resource exhaustion |
| Storage reservations | Quota bypass |
| Concurrent session limits | Upload flooding |
| Temporary upload expiry | Abandoned ciphertext |
| Suspicious-activity signals | Repeated failures |
| Optional CAPTCHA | Friction when needed (not mandatory everywhere) |
| Organisation suspension | Abusive tenants |

Public endpoints rate-limit **before** expensive DB or crypto work. Abuse signals (IPs, user agents) have defined, limited retention.

## 14. Upload session

```json
{ "uploadSessionId": "upl_123", "grantId": "grt_456", "dropboxId": "dbx_789",
  "formSnapshotId": "snap_123", "recipientSnapshotId": "rcp_456",
  "expiresAt": "2026-11-03T13:00:00Z", "limits": { "maxFiles": 10, "maxBytes": 104857600 } }
```

Bound to the verified uploader identity where required. Revalidated before finalisation; uploaded objects and manifests are verified — never trust client-declared success. See [13](13-resumable-uploads.md).

## 15. Uploader UX

Simple and jargon-free (no "grants", "envelopes", "reservations"): validated-invitation and verification status, form fields, file picker with limits, clear progress, expiry info, receipt.

## 16. Receipt and return access

```json
{ "submissionReference": "SI-8K4P2M", "receivedAt": "2026-11-03T12:30:00Z", "publicStatus": "received",
  "returnAccess": { "enabled": true, "mode": "status_only" } }
```

Return access is a **separate capability** from the upload grant. A one-submission invitation never grants access to other submissions in the dropbox. Receipt is downloadable locally or delivered via notifications per policy ([15](15-receipts.md)).

## 17. Audit events

```text
upload_grant.created | updated | revoked | expired
upload_grant.redemption.succeeded | redemption.failed
upload_invitation.issued | delivered | consumed
upload_identity.verified | challenge.failed
upload_session.created | expired | cancelled
upload_abuse.rate_limit_triggered | grant_suspended
```

Never raw bearer tokens, OTPs, PINs or credentials. Anonymous capability holders are distinguished from verified identities.

## 18. Data model

```text
upload_access_grants          upload_grant_redemptions
upload_access_grant_secrets   upload_access_sessions
upload_access_grant_policies  upload_identity_challenges
upload_access_grant_usage     upload_grant_reservations
upload_invitations            upload_grant_revocations
upload_invitation_recipients  upload_abuse_signals
upload_invitation_delivery_events
```

Tenant-scoped; secrets as keyed digests only; DB constraints and atomic transactions enforce limits.

## 19. API

```text
GET  /api/organisations/{id}/dropboxes/{dropboxId}/upload-grants
POST /api/organisations/{id}/dropboxes/{dropboxId}/upload-grants
POST /api/organisations/{id}/upload-grants/{grantId}/revoke
GET  /api/organisations/{id}/upload-grants/{grantId}/usage
POST /api/organisations/{id}/dropboxes/{dropboxId}/invitations
GET  /api/organisations/{id}/invitations
POST /api/organisations/{id}/invitations/{invitationId}/revoke

POST /api/public/upload-grants/redeem
POST /api/public/upload-access-sessions/{sessionId}/verify
POST /api/public/upload-access-sessions/{sessionId}/upload-sessions
```

## 20. Implementation order

1. Unified grant model.
2. Secure token generation and digest storage.
3. Shared links.
4. Redemption.
5. Short-lived access sessions.
6. Individual invitations.
7. Email and OIDC identity binding.
8. Atomic usage reservations.
9. Revocation and expiry.
10. Abuse controls and monitoring.
