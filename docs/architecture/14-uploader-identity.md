# Uploader Identity

Source: notes v3.3. Builds on the four uploader verification modes (anonymous, verified email, one-time code, OIDC) referenced in [12](12-upload-links-and-invitations.md). Resolved questions: [QUESTIONS.md](../QUESTIONS.md) (Q-UID-*). Planning decisions applied below take precedence over the source notes.

> **Planning decisions applied (2026-11-03)** — these override the text below where they differ. See [QUESTIONS.md](../QUESTIONS.md).
> - v1.0 supports **anonymous and one-off verified** uploaders only. Reusable identities come in v1.2. Telephone and SMS verification aren't planned (Q-UID-1).

## Decision

Uploaders may submit **anonymously**, **verify identity for a single submission**, or use an **optional reusable identity** for future submissions. Implemented as a dedicated **Uploader Identity subsystem**, separate from reviewer and administrator identities.

> An uploader identity establishes who is submitting. It never automatically grants access to previous submissions, uploaded files or other organisations. Authentication convenience must not become implicit authorisation.

## 1. Architecture

```text
Public upload access (shared link · invitation · API-issued capability)
   ├── Anonymous (no account)
   ├── One-off (verified for one grant)
   └── Reusable (persistent identity)
        ↓
Organisation-scoped identity binding (verified contacts · invitation eligibility · explicit submission links)
        ↓
Submission access (separate grant · return capability · public status · append permission)
```

## 2. Modes

| Mode | Persistent account | Contact verification | Cross-submission history |
| --- | --- | --- | --- |
| Anonymous | No | Not required | No |
| One-off verified | No | Per grant/session | No automatic linking |
| Reusable identity | Optional | Stored verification evidence | Explicitly authorised submissions only |

A verified email ≠ legal identity; OIDC proves control of an IdP account at that provider's assurance level. Never described as government ID verification.

## 3. Scope and cross-organisation privacy

```text
Uploader Authentication Identity (global opaque subject, e.g. auth_123)
  ├── Organisation A relationship → upl_A_456 (verified contact, submission grants)
  └── Organisation B relationship → upl_B_789 (separate contact policy, grants)
```

Organisation APIs expose only the **organisation-scoped** ID — never a global identifier enabling cross-organisation correlation. The same email in two organisations never merges their records.

## 4. Lifecycle

```text
Created → Pending Verification → Active → Suspended → Closed   (+ pending deletion)
```

| Action | Result |
| --- | --- |
| Create | Establish authentication subject |
| Verify contact | Add verified contact evidence |
| Link organisation | Create scoped relationship |
| Suspend | Block new authenticated actions |
| Revoke sessions | Invalidate credentials |
| Close | Disable identity; remove optional profile data |
| Request deletion | Evaluate retention and legal obligations |

Closing an account never deletes an organisation's legally retained submissions; the UI distinguishes **account closure** from **submission deletion**.

## 5. Authentication

| Method | Support |
| --- | --- |
| Email one-time code | Yes (**recommended default**) |
| Passkeys | Yes, where supported (**recommended default**) |
| External OIDC | Yes |
| Password | Optional (never mandatory) |
| TOTP | Optional or policy-required |
| Recovery codes | Built-in accounts |

Secure host-only cookies, CSRF protection, bounded sessions. Custom upload domains complicate auth: a central auth origin is allowed but **no broad cross-domain cookies** — use an OIDC-style redirect or short-lived authorisation exchange.

## 6. Verified contacts

```text
uploader_contacts(id, uploader_identity_id, type, normalised_value, verification_status,
  verified_at, verification_method, verification_expires_at, revoked_at)
```

Email first; telephone later. Email verification: short-lived challenge; rate-limited creation and attempts; store only a digest of the secret; atomic verify-and-consume; record evidence and expiry; reverify per organisation policy. Verification is never assumed permanent (addresses change hands).

### Contact privacy

Contact details are personal data: encrypted at rest, permission-scoped. Exact-match lookup uses a separate **keyed digest** of the normalised value. The server can still read values for delivery/account management — this is **not** E2EE.

Organisation A can't learn whether an email belongs to an uploader in Organisation B. No global contact lookup via tenant APIs; internal deduplication must not create an enumeration endpoint.

## 7. Invitation binding

| Binding | Behaviour |
| --- | --- |
| None | Bearer invitation, subject to configured challenges |
| Email | Verify the specified email |
| OIDC subject | Exact issuer/subject match |
| Reusable uploader identity | Authenticate as that identity |
| One-off verified contact | Verify without creating an account |

```json
{ "invitationId": "inv_123", "dropboxId": "dbx_456",
  "identityBinding": { "type": "verified_email", "value": "customer@example.com" },
  "allowOneOffVerification": true, "allowReusableIdentity": true, "expiresAt": "2026-11-10T12:00:00Z" }
```

Invitations never auto-create accounts. Invitation restrictions stay authoritative even when the uploader signs in with a reusable account.

## 8. Cross-submission linking

Explicit only. An anonymous submission doesn't appear in an account just because the account later verifies the same email. Linking an existing submission requires proof of the original submission capability, an authorised organisation-controlled process, or an approved recovery mechanism. **Email equality is insufficient.** A return-access token can support linking only if its scope and assurance meet policy.

## 9. Submission history

Limited view of **explicitly linked** submissions: reference, date, organisation name, public status, permitted return actions, expiry/closure. Never internal states, reviewer identities, notes or encrypted files. Default view is per selected organisation; a cross-organisation view needs explicit uploader opt-in.

## 10. Organisation isolation

| Layer | Isolation |
| --- | --- |
| Authentication | Global opaque subject |
| Organisation relationship | Tenant-scoped uploader ID |
| Submission linkage | Tenant-scoped authorisation |
| Database | Tenant routing and RLS |
| API | Organisation context resolved server-side |
| Search | No cross-tenant uploader lookup |
| Audit | Organisation-scoped events |
| Notifications | Organisation-approved contact usage |

## 11. Uploader self-service controls

View/add/remove contacts; manage passkeys and sessions; revoke devices; close the identity; request deletion of optional profile data; review organisation relationships where appropriate.

## 12. Recovery

Email OTP accounts recover via continued email control; passkey users should register several passkeys; total credential loss uses a controlled recovery process. Support staff can't bypass verification on name/email alone. Recovery is audited. **Recovering an uploader account never recovers reviewer encryption keys** — separate systems.

## 13. Enumeration protection

Neutral responses ("If this address is eligible, we'll send instructions."); rate-limit by account, contact digest, IP and other signals; minimise timing/response differences.

## 14. Draft recovery integration

```text
Sign in → list authorised unfinished drafts → select → identity checks if required
→ retrieve encrypted draft → unlock local draft encryption material
```

Authentication alone never yields the plaintext secret for E2EE draft fields; device-local draft keys may preclude cross-device recovery without a portable recovery package ([13](13-resumable-uploads.md)).

## 15. Model

```csharp
public sealed record UploaderAuthenticationSubject(Guid Id, string AuthenticationProvider, string ProviderSubject);

public sealed record OrganisationUploaderIdentity(
    Guid Id, Guid OrganisationId, Guid AuthenticationSubjectId, UploaderIdentityStatus Status);

public enum UploaderIdentityStatus { PendingVerification, Active, Suspended, Closed }
```

For anonymous/one-off submissions use a nullable subject or a separate identity-binding type — **no fake permanent accounts** to satisfy non-null relationships.

## 16. Data model

```text
uploader_authentication_identities     # control plane
uploader_authentication_credentials    # control plane
uploader_authentication_sessions       # control plane
uploader_contacts
uploader_contact_verifications
organisation_uploader_identities       # tenant data plane
organisation_uploader_identity_links   # tenant data plane
submission_uploader_links              # tenant data plane
uploader_submission_link_requests
uploader_account_recovery_operations
uploader_account_closure_operations
uploader_identity_audit_events
```

No cross-database FKs; stable opaque IDs with explicit consistency workflows.

## 17. API

Uploader account:

```text
GET    /api/uploader/me
GET    /api/uploader/me/contacts
POST   /api/uploader/me/contacts
POST   /api/uploader/me/contacts/{contactId}/verify
GET    /api/uploader/me/sessions
DELETE /api/uploader/me/sessions/{sessionId}
GET    /api/uploader/me/submissions
GET    /api/uploader/me/drafts
POST   /api/uploader/me/submissions/link
POST   /api/uploader/me/close
```

Organisation administration:

```text
GET  /api/organisations/{id}/uploaders
GET  /api/organisations/{id}/uploaders/{uploaderId}
GET  /api/organisations/{id}/uploaders/{uploaderId}/submissions
POST /api/organisations/{id}/uploaders/{uploaderId}/suspend
POST /api/organisations/{id}/uploaders/{uploaderId}/restore
```

Admins never see credentials or sessions belonging to unrelated organisations.

## 18. Audit events

```text
uploader.identity.created | verified | suspended | closed
uploader.contact.added | verified | removed
uploader.session.created | revoked
uploader.organisation.linked | unlinked
uploader.submission.linked | link.rejected
uploader.account.recovery.requested | recovery.completed
```

Opaque IDs; avoid copying contact details.

## 19. Implementation order

1. Separate uploader identity domain model.
2. Email OTP authentication.
3. Organisation-scoped relationships.
4. Verified contact management.
5. Invitation binding.
6. Explicit submission linking.
7. Submission history view.
8. Draft recovery integration.
9. Passkeys and external OIDC.
10. Account closure and privacy workflows.
