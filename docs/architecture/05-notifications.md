# Notifications (Organisation, Operational and Uploader)

Sources: notes v2.2 (notification framework) and v3.4 (uploader notifications). Operator security alerts are covered in [21](21-observability-and-operations.md). Resolved questions: [QUESTIONS.md](../QUESTIONS.md) (Q-NOT-*). Planning decisions applied below take precedence over the source notes.

> **Planning decisions applied (2026-11-03)** — these override the text below where they differ. See [QUESTIONS.md](../QUESTIONS.md).
> - SMTP in v1.0. SES and Microsoft Graph in v1.1 (Q-NOT-2). Webhooks use HMAC-SHA256 in the Standard Webhooks format (Q-NOT-3). Locales: en-AU, en-GB, en-US (Q-NOT-4).
> - Public reference format: `SI-XXXXX-XXXXX`, unique per deployment (Q-NOT-1, [00](00-foundations.md) F9).
> - Webhook volume is a rate limit with backpressure (10,000/hour per organisation). Deliveries are delayed, never dropped.
> - Optional uploader notification preferences arrive in v1.1. Mandatory security notices are in v1.0.

## Decisions

1. A **configurable notification framework**: email is the primary human channel; **webhooks** serve integrations and automation.
2. **Organisations control** which uploader notifications are available; **uploaders manage preferences** for optional ones. **Mandatory security and identity notices can't be disabled** through ordinary preferences.

Hard rule: notifications are separate from file delivery. They may say a submission exists but **never attach files, include decryption keys or generate public download links**. The subsystem never needs to decrypt files or E2EE form values.

## 1. Architecture

```text
Submissions · Security · Identity · Operations
          ↓
Durable event outbox (committed with the business transaction)
          ↓
Notification policy resolver / router (deployment rules · organisation settings · uploader preferences · templates)
          ↓
Recipient and privacy validation (verified contact · disclosure policy · template version · suppression)
          ↓
Email · Webhooks · Portal notifications · Internal alerts
          ↓
Delivery tracking and audit (queued · sent · failed · suppressed · acknowledged where supported)
```

Business services emit typed events — never call SMTP/webhooks directly — so notification failures can't break business operations. For security events, the action and its audit record commit **before** anyone is notified.

## 2. Categories

| Category | Examples | Opt-out |
| --- | --- | --- |
| Security | New admin, key revoked, recovery approved, suspicious access; uploader account recovery, credential changes | No (for designated recipients) |
| Identity and access | Email verification, OTP, invitations, access changes | No, when required for the operation |
| Transactional | Submission receipt, failed finalisation, draft expiry | Depends on essentiality |
| Submission / optional status | Received, in review, more information required, completed | Configurable / yes for uploaders |
| Operational | Storage failures, quota warnings, webhook failures | Configurable with enforced minimums |

Transactional ≠ mandatory. Mandatory classification is set by **platform policy** and constrained organisation config — never arbitrary admin labels. Organisations choose designated security contacts and channels but can't suppress platform security alerts.

## 3. Event registry

Stable name, version and documented payload schema per event. Internal domain events are separate from externally published webhook events.

Organisation / operational examples:

```text
organisation.invitation.created   organisation.activated        organisation.suspended
submission.received               submission.revision.added     submission.status.changed
submission.withdrawal.requested   dropbox.expiring              dropbox.expired
security.admin.added              security.api_credential.revoked
security.encryption_key.revoked   security.recovery.requested   security.recovery.approved
quota.storage.warning             quota.storage.exceeded        storage.connection.failed
webhook.delivery.failed           audit.integrity.failed
```

Uploader catalogue:

| Event | Classification | Email | Portal |
| --- | --- | --- | --- |
| `submission.received` | Transactional | Configurable | Yes |
| `submission.revision.added` | Transactional | Configurable | Yes |
| `submission.status.changed` | Optional | Opt-in/configurable | Yes |
| `submission.more_information_required` | Action-required | Configurable | Yes |
| `submission.completed` | Optional | Configurable | Yes |
| `submission.closed` | Optional | Configurable | Yes |
| `upload.draft.expiring` | Optional | Configurable | Yes |
| `upload.draft.expired` | Transactional | Configurable | Yes |
| `uploader.contact.verification` | Mandatory for verification | Yes | Not sufficient alone |
| `uploader.account.security_changed` | Mandatory | Where appropriate | Yes |
| `uploader.account.recovery` | Mandatory | Yes | Yes |
| `upload.invitation.issued` | Access-related | Yes | Where applicable |

## 4. Email providers

- **SMTP with TLS** in the first release (self-hosting friendly) plus a generic HTTPS provider adapter; specific integrations added as needed.
- Validate TLS and certificate trust. **Never silently fall back to unencrypted SMTP.**

```json
{
  "notifications": {
    "email": {
      "enabled": true, "provider": "smtp",
      "fromAddress": "notifications@example.com", "fromName": "Secure Intake",
      "smtp": { "host": "smtp.example.com", "port": 587, "security": "starttls",
                "usernameSecret": "email/username", "passwordSecret": "email/password" }
    }
  }
}
```

### Organisation sender identity

Optional, subject to deployment policy (e.g. `From: Secure Documents <documents@example.org>`, `Reply-To: support@example.org`). Requires domain verification and SPF/DKIM/DMARC; no impersonation of arbitrary domains. Default: the platform's verified sender. See [06](06-branding-and-custom-domains.md).

## 5. Templates

Versioned, per event, scoped by **notification type · channel · locale · organisation · version**. HTML **and** plain text.

```json
{ "templateKey": "submission.received", "channel": "email", "locale": "en-AU", "version": 2,
  "variables": ["organisation.display_name", "submission.public_reference", "submission.received_at", "portal.return_url"] }
```

- Variables are explicitly **allowlisted per event type**; no arbitrary object properties.
- No JavaScript, executable expressions, remote CSS or untrusted HTML. Sanitised templates with safe encoding.
- Branding limited to safe fields (display name, logo, approved colours).

### Variable disclosure classification

| Classification | Example | External email |
| --- | --- | --- |
| Public-safe | Organisation name | Allowed |
| Transactional | Submission reference | Policy-controlled |
| Personal | Verified contact name | Restricted |
| Confidential | E2EE form value | Prohibited |
| Internal | Reviewer notes | Prohibited |
| Security secret | OTP, reset credential | Only via dedicated purpose-bound security templates |

OTPs and recovery credentials are never ordinary template variables; the general template editor never sees them.

### Localisation

Locale-specific templates from the start (`templates/en/`, `en-AU/`, `ja/`...). Fallback: recipient/uploader preference → organisation default → deployment default. Only platform-owned text is localised; user content and E2EE values are never translated.

## 6. Privacy

Email is not E2EE. Payload sensitivity levels: **public-safe** (generic confirmation, reference), **organisation-internal** (submission IDs, operational metadata), **security-sensitive** (restricted security details).

Uploader emails **may** contain: organisation display name; generic event description; submission reference (if policy permits); date/time; safe link to the authenticated portal; support contact.

Uploader emails **must not** contain: filenames (by default); E2EE form values; internal notes; internal workflow states; reviewer names; recipient key IDs; confidential descriptions; public download links.

> Your secure submission has been received. Reference: SI-8K4P2M. … For security, this email does not contain your uploaded documents or confidential submission details.

Return-access links are generated from the applicable capability policy ([12](12-upload-links-and-invitations.md)), never improvised. Delivery logs keep metadata, not full bodies indefinitely.

## 7. Routing

Policy-based:

```json
{ "eventType": "submission.received",
  "channels": [ { "type": "email", "recipient": "dropbox_managers" },
                { "type": "webhook", "subscription": "integration_primary" } ] }
```

- Recipients resolved at dispatch or captured as a versioned snapshot. **Security notifications capture recipients at event creation** so later policy changes can't suppress them.
- Deduplication, suppression windows and escalation policies supported.
- Operational alerts use the same framework with separate routing. Critical alerts must not depend solely on the failing subsystem (external monitoring must still detect e.g. audit persistence failure).

## 8. Uploader policy hierarchy

```text
Deployment policy        available channels · mandatory security categories · disclosure limits · delivery hard limits
  → Organisation policy  enables optional types · chooses approved templates · sets defaults
  → Uploader preferences opt in/out of permitted optional types · choose verified contacts
  → Event eligibility    recipient identity · submission access · contact verification · expiry · security state
```

An uploader can't enable what the organisation prohibits; an organisation can't disable platform-required security notices. With no verified/safe destination, record the limitation instead of sending to an unverified address.

Versioned organisation policy (`mandatory` is **read-only**, derived from platform policy):

```json
{ "policyVersion": 3, "defaultLocale": "en-AU",
  "channels": { "email": true, "portal": true },
  "notifications": {
    "submission.received":                  { "enabled": true, "mandatory": false, "defaultPreference": true },
    "submission.status.changed":            { "enabled": true, "mandatory": false, "defaultPreference": false },
    "submission.more_information_required": { "enabled": true, "mandatory": false, "defaultPreference": true } } }
```

### Uploader preferences

- Reusable identities: persistent preferences, scoped per organisation (off for Org A, on for Org B), with optional submission-level overrides.
- One-off uploaders: per-submission preferences without an account.
- Anonymous uploaders without a verified contact: notifications via the scoped return-access portal, where enabled.

```json
{ "submissionUpdates": true, "completionNotices": true, "draftReminders": false,
  "moreInformationRequests": true, "preferredChannel": "email", "preferredLocale": "en-AU" }
```

```csharp
public interface IUploaderNotificationPolicyResolver
{
    ValueTask<NotificationDecision> ResolveAsync(NotificationContext context, CancellationToken ct = default);
}
public sealed record NotificationContext(Guid OrganisationId, Guid? UploaderIdentityId, Guid? SubmissionId, string NotificationType, string Channel);
public sealed record NotificationDecision(bool Allowed, bool Mandatory, string ReasonCode, Guid PolicyVersionId);
```

Queued optional notifications **recheck preferences immediately before delivery** (respect recent opt-outs). Mandatory security notices use the event's security policy with current destination-safety checks.

### Contact verification states

| State | Allowed use |
| --- | --- |
| Unverified | Verification challenge only |
| Verified | Approved notifications |
| Verification expired | Reverify where policy requires |
| Revoked | None |
| Suppressed | No ordinary delivery |
| Bounced | Blocked pending correction |
| Compromised | Security workflow decides safe alternatives (never blindly send recovery to it) |

One-off verification binds to the submission/invitation and never implicitly creates a reusable global identity ([14](14-uploader-identity.md)).

## 9. Portal notifications

Minimal notification centre for reusable uploaders. Anonymous/one-off uploaders reach notifications **only** through the submission-specific return capability. The centre must never enable enumerating submissions by email address.

## 10. Public status deduplication

Internal transitions (Assigned → Manager review → Legal review → Pending approval) may all map to public `in_review`. Send a public status notification **only when the effective public status changes**, unless a distinct action-required event fires. Avoids noise and leaking internal workflow ([10](10-review-workflows.md)).

## 11. Additional-information requests

The platform stays inbound-only: the email says more information is needed; the uploader follows the scoped return link and uploads a new encrypted **revision**. Nothing is sent back. Request text is limited to approved uploader-visible instructions — never internal notes.

## 12. Uploader security notices

| Event | Notification |
| --- | --- |
| Password changed | Existing verified contact |
| Passkey added/removed | Verified contact |
| Recovery initiated / completed | Eligible existing contact |
| Verified contact changed | Old and new contacts where safe |
| Suspicious sign-in | Risk-based notice |
| Account suspended | When safe and appropriate |
| Sessions revoked | Where configured |

Generated by dedicated security workflows; not suppressible by organisation admins; delivery to compromised/unverified destinations handled safely.

## 13. Webhooks

HTTPS only in production. Subscription: organisation owner; endpoint URL; event types; signing secret or asymmetric verification; enabled state; timeout; retry policy; last success; failure status.

```json
{ "id": "evt_01JEXAMPLE", "type": "submission.received", "version": 1,
  "occurredAt": "2026-11-03T04:00:00Z", "organisationId": "org_123",
  "data": { "submissionId": "sub_456", "dropboxId": "dbx_789", "reference": "SI-8K4P2M" } }
```

Identifiers and minimal metadata only; integrations fetch authorised ciphertext via the authenticated API.

**Signing:** HMAC-SHA256 over timestamp + exact payload bytes with unambiguous framing; signature header carries a key ID for rotation. Receivers verify in constant time, enforce timestamp tolerance, and dedupe on event ID.

**SSRF protection:** HTTPS only; DNS resolution + IP validation; block loopback, link-local, private and metadata addresses unless a trusted deployment policy allows; redirects off by default; revalidate on every connection; connection/response-size/timeout limits; no internal credential forwarding; network egress restrictions where possible. URL validation alone is insufficient (DNS rebinding, proxies). High-isolation deployments may run webhook delivery in a restricted worker.

Test deliveries are marked as tests and use synthetic data only.

## 14. Delivery reliability

```text
Event committed → Routing evaluated → Delivery queued (one record per destination)
→ Attempt → Confirm or retry → Escalate (dead-letter)
```

Uploader delivery states: `Pending → Eligible → Queued → Sending → Sent`, alternatives `Suppressed`, `Failed`, `Retry scheduled`, `Dead-lettered`, `Cancelled`.

**At-least-once**, never exactly-once. Webhook consumers dedupe by event ID; email uses provider idempotency and stable message IDs. Provider acceptance ≠ delivery or reading.

| Failure | Behaviour |
| --- | --- |
| HTTP 429 | Retry with bounded `Retry-After` |
| HTTP 5xx, timeout | Exponential backoff with jitter |
| DNS failure | Retry, then escalate |
| HTTP 401/403 | Pause or fail per policy |
| HTTP 404/410 | Permanent after configured checks |
| Invalid webhook config | Disable and alert |
| SMTP temporary | Retry |
| SMTP permanent rejection | Stop; record |

Configurable retry budgets within deployment limits. A failing webhook never blocks unrelated events or organisations.

## 15. Administration UI

| Page | Functions |
| --- | --- |
| Email settings | Sender identity, provider status, test delivery |
| Templates | Preview, localisation, controlled customisation |
| Event subscriptions | Events and recipients |
| Webhooks | Endpoints, signing secrets, test delivery |
| Delivery history | Attempts, status, failure details |
| Alerts | Thresholds, routing, escalation |
| Uploader notification policy | Enable optional types, defaults |

Uploader-facing **Notification Preferences** page shows only organisation-enabled options; essential security notices are shown as "Required".

## 16. Data model

```text
notification_providers           notification_delivery_attempts
notification_templates           notification_suppressions
notification_template_versions   notification_alert_policies
notification_routes              webhook_subscriptions
notification_preferences         webhook_signing_keys
notification_events              notification_recipient_snapshots
notification_deliveries          notification_privacy_classifications
                                 notification_suppression_reasons

organisation_uploader_notification_policies
organisation_uploader_notification_policy_versions
uploader_notification_preferences
uploader_submission_notification_preferences
uploader_notification_inbox
uploader_notification_read_states
```

Uploaders reuse the same delivery infrastructure. In hybrid deployments, tenant events may originate in dedicated DBs while delivery workers run centrally; the outbox relay preserves tenant routing and event identity.

## 17. API

```text
GET  /api/organisations/{id}/notifications/settings
PUT  /api/organisations/{id}/notifications/settings
GET  /api/organisations/{id}/notification-templates
PUT  /api/organisations/{id}/notification-templates/{templateId}
GET  /api/organisations/{id}/notification-routes
PUT  /api/organisations/{id}/notification-routes/{routeId}
GET  /api/organisations/{id}/webhooks
POST /api/organisations/{id}/webhooks
POST /api/organisations/{id}/webhooks/{webhookId}/test
GET  /api/organisations/{id}/notification-deliveries
POST /api/organisations/{id}/notification-deliveries/{deliveryId}/retry
GET  /api/organisations/{id}/uploader-notification-policy
PUT  /api/organisations/{id}/uploader-notification-policy

GET  /api/uploader/me/notification-preferences
PUT  /api/uploader/me/notification-preferences
GET  /api/uploader/me/notifications
POST /api/uploader/me/notifications/{notificationId}/read
GET  /api/public/submissions/{submissionId}/notifications     # requires submission return capability
```

Organisation admins can't access other organisations' uploader preferences or contact data.

## 18. Audit events

```text
uploader.notification.preference_changed | policy_changed
uploader.notification.queued | sent | failed | suppressed
uploader.notification.template_published | template_changed
uploader.notification.contact_rejected
uploader.notification.security_notice_issued
```

Capture policy version, template version, recipient reference, outcome, reason codes. Never raw OTPs, return tokens, confidential form values or rendered bodies by default.

## 19. Implementation order

1. Typed event registry, classification and outbox integration.
2. SMTP provider.
3. Built-in transactional templates (invitation, verification) with privacy allowlists.
4. Submission notifications and receipts.
5. Webhook subscriptions and HMAC signing (with SSRF controls).
6. Retry, dead-letter and delivery history.
7. Organisation uploader-notification policy and verified recipient resolution.
8. Additional-information request notifications.
9. Uploader preferences and public status deduplication.
10. Operational alert routing.
11. Portal notification centre.
12. Template customisation, additional providers, escalation routing; security-notice monitoring.
