# Reviewer Key Custody, Portability and Recipient Assignment

Sources: notes v2.6 (policy-controlled key portability) and v2.7 (hybrid individual/team recipients). Lifecycle governance, trust registries and compromise handling are in [24-key-governance.md](24-key-governance.md). Resolved questions: [QUESTIONS.md](../QUESTIONS.md) (Q-KCR-*). Planning decisions applied below take precedence over the source notes.

> **Planning decisions applied (2026-11-03)** — these override the text below where they differ. See [QUESTIONS.md](../QUESTIONS.md).
> - The Hybrid default is confirmed. v1.0 expands teams into individual envelopes, and shared team keys arrive in v1.2 (Q-KCR-1).
> - A **recovery recipient is required by default** for new organisations (Q-KCR-2).
> - Max devices 5 (Hardened 3). Key expiry 2 years (Hardened 1 year) (Q-KCR-3).
> - `.sik` export and import ship in the CLI in v1.0 and in the browser in v1.1. KMS/HSM adapters are v1.2.

## Decisions

1. **Each organisation controls** how its reviewers' private keys are stored, exported, backed up and used across devices, choosing from three custody models: **device-bound**, **encrypted portable**, **externally managed** (KMS/HSM). **Default: device-bound.**
2. **A dropbox may encrypt submissions for individual reviewers, organisation-controlled team recipients, or both.**

Two independent mechanisms; **both** must be satisfied to retrieve and decrypt via the platform:

- **Authorisation** — who may view submissions, retrieve ciphertext and perform reviewer operations.
- **Cryptographic access** — which private keys can decrypt each file.

Adding a reviewer to a team must never silently grant decryption of that team's historical files.

Software-only controls can't guarantee non-exportability when the user/device admin controls the execution environment; policy is enforced by clients and server as governance, not absolute prevention.

---

## Part A — Key custody and portability

### A1. Custody architecture

```text
Organisation key policy (permitted custody · export permissions · backup · recovery)
   ├── Device-bound  — local or hardware-protected key
   ├── Portable      — encrypted key package (.sik)
   └── External      — organisation-controlled key service
Common encryption protocol (public-key registration · recipient envelopes · ciphertext decryption)
```

Custody is a property of each **key** (governed by policy), not the reviewer account. A reviewer may hold several keys with different custody.

### A2. Organisation key policy (versioned)

| Setting | Values |
| --- | --- |
| Allowed custody | Device-bound, portable, external |
| Default custody | One of the allowed modes |
| Private-key export / import | Disabled, permitted, approval required |
| Max active devices | Configurable |
| MFA for enrolment | Required by default |
| MFA for export | Required |
| Independent export approval | Optional or mandatory |
| Key backup | Optional, required, prohibited |
| Recovery | Disabled, organisation key, external custody |
| Device attestation | Optional, where supported |
| Key expiry | Configurable |
| Revocation authority | Security Officer / authorised administrator |

```json
{ "policyVersion": 3,
  "allowedCustodyModes": ["device_bound", "encrypted_portable", "external"],
  "defaultCustodyMode": "device_bound",
  "exports": { "enabled": true, "requiresMfa": true, "requiresIndependentApproval": true },
  "imports": { "enabled": true, "requiresMfa": true },
  "devices": { "maximumActive": 5 },
  "backup": { "requiredForPortableKeys": true } }
```

Policy changes apply to new operations immediately but can't retroactively un-export a key; tightening to device-bound uses a migration/retirement workflow rather than deleting portable keys. The server records the policy version authorising each sensitive operation.

### A3. Device enrolment

Distinct from authentication and key registration — signing in on a device doesn't authorise it for decryption.

```text
reviewer_devices(id, organisation_id, reviewer_identity_id, display_name, device_type, client_type,
  client_version, public_device_key, attestation_status, enrolled_at, last_seen_at, revoked_at)
```

Device name/OS/version are operational metadata, not security evidence unless verified.

1. Authenticate (IdP + MFA). 2. Register device (client creates device ID + device-bound credential). 3. Evaluate policy (limits, allowed clients, attestation). 4. Generate or import key. 5. Prove possession (suite-specific challenge). 6. Activate (record device, key assignment, approvals).

**One key pair per device** for device-bound custody (better compromise containment and retirement). Portable keys may sit on several devices, weakening device-level revocation.

### A4. Encrypted export (`.sik`)

```text
Secure Intake Key Package (.sik)
├── Format identifier          ├── Key-encryption parameters
├── Package version            ├── Salt and nonce
├── Encryption suite           ├── Encrypted private-key material
├── Key identifier             └── Authentication tag
├── Public-key fingerprint
└── Key metadata
```

- Payload and interpretation-affecting metadata are authenticated and encrypted.
- Passphrase export: reviewed KDF such as **Argon2id** + approved AEAD; versioned, **bounded** parameters (malicious packages mustn't exhaust clients).
- Optional wrapping to an organisation-approved recovery/transfer public key where the suite allows. **No invented wrapping constructions.**

Export is sensitive: active auth; recent MFA; organisation export permission; policy validation; explicit key selection; destination confirmation; reason where required; independent approval if configured; audit. The server authorises via a short-lived, one-time, session-bound, cryptographically protected authorisation and **never receives the private key** — the client exports locally.

```json
{ "operation": "reviewer_key.export", "organisationId": "org_123", "reviewerId": "usr_456",
  "keyId": "key_789", "approvalId": "approval_123", "expiresAt": "2026-11-03T12:00:00Z" }
```

A compromised client can copy an exportable software key without the workflow; these are governance controls.

### A5. Import

Checks: supported package version and suite; valid bounded structure; successful authenticated decryption; public/private consistency; expected fingerprint; organisation policy; duplicate detection; device registration and permissions. Imported keys keep their identity; the platform creates a new device–key association (shows known holders, can't prove no unregistered copies exist).

### A6. Backup

| Strategy | Description | Limitation |
| --- | --- | --- |
| Encrypted local backup | Reviewer exports `.sik` | Reviewer protects backup and passphrase |
| Organisation-controlled backup | Package in approved external custody | Organisation manages access/recovery |
| Device-bound, no export | No backup | Device loss may permanently lose the key |

No server-side plaintext key backup. Server-stored opaque encrypted backups are possible only if explicitly enabled — **out of scope for the first release**.

### A7. Recovery

**Reviewer key recovery** restores a specific private key (from backup/external custody). **File recovery** uses a separately authorised organisation recovery recipient to recover a file key — it doesn't restore the reviewer's key.

| Scenario | Outcome |
| --- | --- |
| Device lost; portable backup exists | Import on replacement |
| Device lost; external custody | Re-enrol via provider |
| Device-bound key lost | New key; historical access needs another recipient or recovery |
| All recipient keys lost; recovery enabled | Recover via approved recovery process |
| All keys lost; recovery disabled | Files may be **permanently unrecoverable** |

Make these consequences explicit during onboarding.

### A8. Revocation layers

| Revocation | Effect |
| --- | --- |
| Device | Blocks future authenticated operations from the device where enforceable |
| Session | Invalidates sessions and tokens |
| Reviewer key | Not used for new envelopes |
| Membership removal | Blocks future platform authorisation |
| Recovery key | No new recovery envelopes |
| Storage credential | Blocks affected storage access |

Revocation never deletes keys or plaintext already outside the platform. Per-device keys let a compromised device's key be retired without retiring others — the reason device-bound is the default.

### A9. Rotation

New key identity; never overwrite the old public key (`key_001 active → key_001 retiring + key_002 active → key_001 retired`). New submissions use the active set; existing files keep historical envelopes unless rewrapped or re-encrypted. Track file↔key associations for impact analysis.

### A10. Cross-client portability (`.sik`)

| Source → Destination | Portable key |
| --- | --- |
| Browser → Desktop / CLI | Supported |
| Desktop → Browser | Supported, subject to policy and suite |
| Desktop ↔ CLI | Supported |
| External KMS/HSM → Browser | Not assumed |
| Device-bound OS key → another device | Not assumed (enrol a new key) |

Clients advertise capabilities and reject unsupported operations clearly; never silently convert a device-bound/external key into a portable software key.

### A11. Keys & Devices admin area

Shows key status, device status, custody mode, exportability, backup status (only "verified" after a restore test), last use, suite, recipient groups, historical file coverage.

---

## Part B — Recipient assignment

### B1. Recipient architecture

```text
Dropbox recipient policy (eligible individual, team, recovery recipients)
  → Server-approved recipient snapshot (specific keys · versions · suites · policy version)
  → Browser encrypts the file key: one authenticated envelope per selected key
       ├── Reviewer (individual key)
       ├── Team (group key)
       └── Recovery (custody key)
```

The uploader's client encrypts; the server supplies policy and public keys but never sees the plaintext DEK.

### B2. Assignment modes

| Mode | Recipients | Use case |
| --- | --- | --- |
| Individual | Selected reviewer keys | Confidential case-specific submissions |
| Team | Organisation-controlled team keys | Shared review queues |
| Hybrid | Individual + team | Flexible team workflows |
| Organisation | Organisation intake key | Centralised intake, later assignment |

Recommended dropbox default: **Hybrid**, with a designated intake team as primary recipient (no need to know the handling reviewer in advance). **Confirmed (Q-KCR-1):** v1.0 implements Hybrid by expanding the team into individual member envelopes, plus the recovery recipient. Shared team keys arrive in v1.2.

### B3. Recipient policy and snapshot

```json
{ "dropboxId": "dbx_123", "policyVersion": 4, "mode": "hybrid",
  "teamRecipients": [ { "teamId": "team_finance", "required": true } ],
  "individualRecipients": [ { "reviewerId": "usr_456", "required": false } ],
  "recoveryRecipient": { "enabled": true, "required": true },
  "historicalAccess": "explicit_rewrap" }
```

Resolved into concrete key IDs at upload-session start; the snapshot is **immutable for the session**. If a key is revoked before finalisation, revalidate and reject finalisation or require a new encryption session — **never substitute keys after encryption**.

Authenticated recipient manifest:

```json
{ "uploadSessionId": "upl_123", "organisationId": "org_123", "dropboxId": "dbx_123",
  "recipientPolicyVersion": 4, "expiresAt": "2026-11-03T12:00:00Z",
  "recipients": [
    { "recipientType": "team", "recipientId": "team_finance", "keyId": "key_team_004",
      "suiteId": "approved-suite-id", "publicKeyFingerprint": "sha256:..." },
    { "recipientType": "individual", "recipientId": "usr_456", "keyId": "key_user_008",
      "suiteId": "approved-suite-id", "publicKeyFingerprint": "sha256:..." } ] }
```

The client verifies it; a signature verified with a key from the same possibly-compromised origin isn't enough — higher-assurance clients need an independent trust anchor. The file manifest binds policy version, recipient key IDs and suite to the submission.

### B4. Team membership vs key possession

Membership grants organisational permissions; a **key access grant** records how a reviewer may use the team key.

| Reviewer | Member | Ciphertext access | Team key | Can decrypt |
| --- | --- | --- | --- | --- |
| Alice | Yes | Yes | Yes | Yes |
| Ben | Yes | Yes | No | No |
| Charlie | No | No | Previously copied | Not via platform; possibly offline |
| Dana | Yes | No | Yes | Not via platform |

### B5. Team-key custody

- **A. Organisation-controlled team key** (KMS/HSM/secure service; access via authorised client/controlled operation) — preferred where central revocation matters; provider must support the suite.
- **B. Distributed team key** (encrypted packages on approved devices) — supports offline/browser decryption but removing a member can't guarantee key destruction.

### B6. Individual envelopes for team members (safer default for ordinary teams)

The team is expanded to eligible member keys at upload start (Finance → Alice key_A, Ben key_B, Dana key_D).

| Property | Shared team key | Individual member envelopes |
| --- | --- | --- |
| Envelope count | Fewer | Grows with team size |
| New member historical access | Possible if given team key | Explicit rewrap |
| Removed member | Shared key may remain usable | Existing envelope remains usable |
| Compromise isolation | Affects all team files | Only files addressed to that key |
| Offline decryption | Yes if key available | Yes |
| Administration | Central custody | More recipient management |

Both supported; organisation-controlled team keys suit large or high-churn teams.

### B7. Historical access

| Policy | Behaviour |
| --- | --- |
| No historical access | New members only get future submissions |
| **Explicit rewrap** (default) | Authorised client adds envelopes to selected historical files |
| Organisation custody | Members reach historical files via the organisation-controlled team key |

Handing a new member an existing shared team private key gives them historical decryption regardless of a "no historical access" label. Strict separation needs individual envelopes or separate team-key epochs with controlled custody.

### B8. Reassignment (a workflow, not a DB update)

1. Request (authorised manager picks new reviewer). 2. Validate permissions and key eligibility. 3. Determine key coverage (does Ben already have an envelope?). 4. Authorised client **rewraps** if needed. 5. Commit assignment and envelope metadata atomically where possible. 6. Audit and notify.

The server can't rewrap (it lacks the DEK). If no authorised client can decrypt the existing envelope, reassignment may need the recovery process.

### B9. Rewrap vs re-encryption

- **Rewrap:** authorised client decrypts the DEK and wraps the same DEK for another recipient; ciphertext unchanged. For adding legitimate recipients.
- **Full re-encryption:** authorised client decrypts content and re-encrypts under a new DEK. Needed after DEK/recipient-key compromise; can't undo prior disclosure.

### B10. Group-key epochs and rotation

```text
Finance Team: Epoch 1 key_team_001 retired · Epoch 2 key_team_002 retiring · Epoch 3 key_team_003 active
```

New uploads use the active epoch; history keeps original envelopes unless migrated. Triggers: schedule; reviewer departure; suspected compromise; custody-provider migration; suite migration; policy change. Impact assessment (files still depending on the key) before retiring.

### B11. Member removal

1. Remove membership. 2. Revoke sessions/grants. 3. Revoke device or external key-service access. 4. Stop using their individual key for future submissions. 5. Evaluate shared team-key rotation (generally required if they held a distributed team key). 6. Assess historical exposure. 7. Audit.

### B12. Assignment, grant and envelope are different records

- `submission_assignments` — responsibility for review.
- `submission_access_grants` — platform retrieval permission.
- `file_recipient_envelopes` — cryptographic capability.
- `team_key_access_grants` — how a member may use a team key.

```json
{ "submissionId": "sub_123", "assignedTeamId": "team_finance", "assignedReviewerId": "usr_456",
  "accessPolicy": { "allowTeamReviewers": true, "allowAssignedReviewer": true },
  "encryption": { "recipientPolicyVersion": 4, "keyEpoch": 3 } }
```

```csharp
public interface ISubmissionAccessEvaluator
{
    ValueTask<SubmissionAccessDecision> EvaluateAsync(SubmissionAccessRequest request, CancellationToken ct = default);
}
public sealed record SubmissionAccessRequest(Guid OrganisationId, Guid SubmissionId, Guid ActorId, SubmissionAction Action);
public sealed record SubmissionAccessDecision(bool Allowed, string ReasonCode);
```

Checks membership, scoped permissions, assignment, current policy and step-up. An envelope's presence never proves authorisation; the client independently verifies envelope integrity and key compatibility.

### B13. Review Teams & Recipients admin area

Shows members, recipient strategy, current key epoch, historical access policy, recipient coverage (% accessible to active reviewers, % needing rewrap). Clearly separates assignment, retrieval permission and cryptographic access — team membership alone isn't shown as full access.

---

## Data model

```text
organisation_key_policies              review_teams
organisation_key_policy_versions       review_team_memberships
reviewer_devices                       review_team_roles
reviewer_device_credentials            review_team_recipient_policies
reviewer_device_key_assignments        review_team_key_epochs
reviewer_key_export_authorisations     review_team_key_custody_assignments
reviewer_key_import_registrations      dropbox_recipient_policies
reviewer_key_backup_attestations       dropbox_recipient_policy_versions
reviewer_key_recovery_operations       upload_recipient_snapshots
reviewer_key_rotation_operations       submission_assignments
reviewer_key_revocation_events         submission_access_grants
                                       submission_reassignment_operations
                                       file_recipient_envelopes
                                       file_recipient_rewrap_operations
                                       file_reencryption_operations
                                       team_key_access_grants
```

Immutable historical key records and recipient snapshots are kept for audit and compromise-impact analysis. No private-key material in any table.

## API

```text
GET  /api/organisations/{id}/key-policies
PUT  /api/organisations/{id}/key-policies
GET  /api/organisations/{id}/reviewer-devices
POST /api/organisations/{id}/reviewer-devices
POST /api/organisations/{id}/reviewer-devices/{deviceId}/revoke
POST /api/organisations/{id}/recipient-keys/{keyId}/export-authorisations
POST /api/organisations/{id}/recipient-keys/{keyId}/import-registrations
GET  /api/organisations/{id}/recipient-keys/{keyId}/usage
POST /api/organisations/{id}/recipient-keys/{keyId}/rotate
POST /api/organisations/{id}/recipient-keys/{keyId}/revoke

GET    /api/organisations/{id}/review-teams
POST   /api/organisations/{id}/review-teams
PUT    /api/organisations/{id}/review-teams/{teamId}
POST   /api/organisations/{id}/review-teams/{teamId}/members
DELETE /api/organisations/{id}/review-teams/{teamId}/members/{memberId}
GET    /api/organisations/{id}/review-teams/{teamId}/recipient-policy
PUT    /api/organisations/{id}/review-teams/{teamId}/recipient-policy
POST   /api/organisations/{id}/review-teams/{teamId}/rotate-key
GET    /api/organisations/{id}/submissions/{submissionId}/assignments
POST   /api/organisations/{id}/submissions/{submissionId}/reassign
POST   /api/organisations/{id}/files/{fileId}/rewrap-operations
POST   /api/organisations/{id}/files/{fileId}/reencryption-operations
```

The API authorises, coordinates metadata and audits; export/import/rewrap/re-encryption run in authorised clients.

## Security boundaries to document

- Device-bound ≠ hardware-bound; local software keys may be extractable by malware or privileged users.
- Encrypted export ≠ safe custody (weak passphrases, compromised endpoints, copied backups).
- Revocation ≠ retrospective secrecy.
- External custody ≠ post-quantum support — verify KMS/HSM capabilities against the suite.
- Browser and desktop clients have different threat models (web-delivered JS vs software distribution/update security).

## Implementation order

1. Versioned key-custody policy.
2. Device registry and enrolment; per-device key generation.
3. Team and membership model; versioned dropbox recipient policies.
4. Individual recipient expansion; immutable upload recipient snapshots; envelope validation.
5. Submission assignments and access grants.
6. `.sik` specification; desktop/CLI export/import; then browser.
7. Export authorisation and approval.
8. Reassignment workflow and client-side rewrap protocol.
9. Backup verification; device and key revocation.
10. Team-key custody and epochs.
11. Full re-encryption workflow.
12. External KMS/HSM adapters.
