# Organisation Onboarding and Lifecycle

Sources: notes v1.8 (registration, activation, invitations) and v4.5 (unified provisioning). Where they conflict, v4.5 is authoritative; open conflicts are tracked in [QUESTIONS.md](../QUESTIONS.md) (Q-ONB-*).

> **Planning decisions applied (2026-11-03)** — these override the text below where they differ. See [QUESTIONS.md](../QUESTIONS.md).
> - A v1.8 *registration* is the same entity as a v4.5 *provisioning request*. v1.8 tables are dropped (Q-ONB-1).
> - **Security lockdown** invalidates sessions and upload sessions immediately (Q-ONB-3).
> - The offboarding export window is mandatory: 30 days by default, 180 days maximum (Q-ONB-4).
> - Self-service onboarding is **v1.3**. New self-service organisations are restricted for 14 days (Q-ONB-5). v1.0 supports manual, CLI and API provisioning only.
> - Billing is permanently out of scope (Q-ONB-6). Invitation and request expiry defaults are in [27](27-production-defaults.md).

## Decision

Secure Intake supports **manual**, **privileged API/CLI** and **self-service** organisation onboarding through **one unified provisioning pipeline**. Deployment operators decide which methods are enabled. All methods enforce the same tenant isolation, security policy, resource validation and activation requirements.

Principles:

- Creating an organisation is **not** activating it. An organisation must pass provisioning and security checks before it can accept submissions.
- Creating an organisation must never automatically grant permission to accept public uploads.
- The entry point determines initial trust level and approval requirements — not how the organisation is stored or administered.
- No entry point may bypass the provisioning service (e.g. by inserting directly into organisation tables).

## 1. Provisioning methods

| Method | Initiator | Default | Approval |
| --- | --- | --- | --- |
| Manual (Admin UI) | Platform administrator | Enabled | Policy-controlled |
| API | Authorised, scoped service account | Enabled | Policy-controlled |
| CLI | Authorised platform operator | Enabled | Same as manual/API |
| Self-service | Eligible external applicant | **Disabled** | Operator-configurable |

Self-service is opt-in, especially for private/self-hosted installations (an installation used exclusively by one integrating product may never need it).

Rules:

- An API token with organisation-creation permission does **not** gain access to the organisation it creates.
- Self-service registration never grants platform-level privileges.
- The server decides whether a principal may bypass approval; a client-supplied `requireApproval: false` must not override deployment policy.
- These settings are deployment policy. Organisations cannot enable self-service for the platform. An organisation may separately control whether its administrators can invite members.

Illustrative deployment policy:

```json
{
  "organisationProvisioning": {
    "manualEnabled": true,
    "apiEnabled": true,
    "selfServiceEnabled": false,
    "selfServiceRequiresApproval": true,
    "defaultTenantDatabaseProfile": "shared-postgres",
    "defaultStorageProfile": "primary-s3",
    "requireInitialAdministratorMfa": true,
    "selfService": {
      "requireEmailVerification": true,
      "requireMfaBeforeActivation": true,
      "allowedEmailDomains": [],
      "blockedEmailDomains": [],
      "maximumPendingRegistrations": 100
    }
  }
}
```

## 2. Lifecycle state machine

Organisation status is separate from onboarding progress (an organisation may be active with optional onboarding tasks outstanding).

Main path:

1. `requested` — application received; identity and eligibility checked
2. `pending_approval` — waiting for required operator/security approval
3. `provisioning` — resources, identity and policy assignments created
4. `pending_activation` — provisioning complete; final security validation outstanding
5. `active` — may operate under its assigned policies

Other states:

| State | Meaning |
| --- | --- |
| `rejected` | Onboarding request declined (terminal; never becomes an active organisation) |
| `provisioning_failed` | Provisioning stopped with recoverable findings — **not** a rejection |
| `suspended` | Existing organisation temporarily restricted |
| `deletion_pending` | Approved deletion/retention workflow in progress |
| `deleted` | Logical deletion completed, subject to retained evidence and backups |

All transitions are transactional, auditable and concurrency-safe (invitation redemption and activation especially).

## 3. Common creation request

```csharp
public sealed record CreateOrganisationRequest(
    string DisplayName,
    string Slug,
    OrganisationProvisioningMethod Method,   // Manual | Api | SelfService
    Guid? RequestedByPrincipalId,
    string? InitialAdministratorEmail,
    string? DatabaseProfileId,
    string? StorageProfileId,
    string? IdempotencyKey);
```

- Profiles may only be chosen from an operator-approved catalogue. Self-service applicants can never supply connection strings, storage credentials or internal infrastructure identifiers.
- Organisations have an immutable internal ID (`org_01J...`) used for tenancy, audit, encryption context and resource mapping. Display name and slug may change under policy; renaming must not invalidate cryptographic evidence.
- **External references** let an integrating system correlate its tenant without relying on internal IDs: `{ "externalReference": { "system": "swoovr", "value": "tenant-123" } }`. Unique within their namespace.

## 4. Tenant resource assignment

Provisioning resolves two independent assignments — database profile and storage profile — each shared or dedicated. See [03-tenancy-and-isolation.md](03-tenancy-and-isolation.md).

```csharp
public sealed record OrganisationResourceAssignment(
    Guid OrganisationId,
    string DatabaseProfileId,
    string StorageProfileId,
    long AssignmentVersion,
    DateTimeOffset AssignedAt);
```

Once files exist, resource changes require the explicit migration workflow; existing files stay pinned to their storage profile and object identity.

## 5. Provisioning workflow and transaction boundaries

Provisioning spans databases, object stores and identity providers, so no single ACID transaction covers it. Use a **durable workflow of idempotent steps with compensating actions**:

```text
Create organisation record → Reserve slug and identity → Assign database profile
→ Initialise tenant schema → Assign storage profile → Validate storage access
→ Create identity/membership records → Apply security-policy baseline
→ Evaluate key requirements → Run activation checks
```

- Each step records status and retry information; steps have stable IDs and idempotency keys.
- If a later step fails, the organisation remains inactive.
- Compensation is conservative: an unused provisional storage prefix may be cleaned up after verification; a dedicated database containing retained evidence is never deleted automatically by a retry.

## 6. Tenant isolation validation (pre-activation)

A generic connectivity check does not prove isolation.

**Shared PostgreSQL:** organisation ID correctly assigned; RLS policies present; `FORCE ROW LEVEL SECURITY` where required; runtime roles cannot bypass RLS; tenant-scoped queries run under correct context; cross-tenant access tests pass.

**Dedicated PostgreSQL:** correct endpoint; expected deployment identity; correct schema version; tenant ownership metadata matches; credentials restricted to the assigned database.

**Object storage:** profile resolves; read/write/delete capabilities verified safely; namespace isolated; presigned URL policy matches configured host and expiry; credentials scoped as narrowly as the provider allows.

## 7. Initial administrator and invitations

At least one initial administrator is required before activation. Supplying an email address never creates an authenticated administrator; the owner is **invited**, never created with a default password.

```text
Organisation created → Initial administrator nominated → Invitation issued
→ Identity verified → MFA enrolled → Membership accepted → Administrator activated
```

Invitation contents: organisation ID, intended email, intended role, expiry, single-use random token, status and audit reference. The raw token is delivered once and stored as a **keyed hash**.

Invitation states: `pending` → `accepted` | `expired` | `revoked` | `superseded`.

Rules:

- Never silently link an account solely because its email matches.
- Built-in auth: require email verification and normal enrolment. External OIDC: bind membership to verified `issuer + subject` after invitation checks.
- A person in another organisation may accept using the same internal user identity, without merging permissions.
- Don't place raw tokens in URL paths (log capture). Use a landing page that redeems the token via a protected POST.
- The initial administrator receives only the standard **Owner** role. JIT elevation and independent approvals still apply ([22-access-control.md](22-access-control.md)). No automatic access to E2EE private keys.

## 8. Identity setup

Supported: built-in identity, deployment-wide OIDC, organisation-specific OIDC (where enabled).

- Permanent identity key is `issuer + subject`, never email alone.
- Organisation OIDC is configured through an authorised setup workflow: issuer validation, trusted metadata retrieval, redirect URI restrictions, administrative approval where policy requires.
- An organisation must not register an issuer that impersonates another organisation's trusted identity domain.

## 9. Verification facts

Store independently verified facts rather than a single "verified" flag:

| Verification | Meaning |
| --- | --- |
| Email verified | Applicant controls the mailbox |
| Domain verified | Demonstrated control of a DNS domain (≠ legal registration) |
| Administrator authenticated | Owner completed required authentication |
| Platform approved | Operational decision by a platform reviewer (≠ legal identity) |
| Billing verified | Payment arrangement confirmed, if applicable |
| Legal identity verified | Separate business verification, if supported |

Initial release requires only email verification, owner authentication and any deployment-required platform approval. Domain and business verification are optional, later capabilities.

## 10. Key enrolment vs activation

- **Organisation activation**: administrative and infrastructure setup complete.
- **Dropbox publication**: required recipient-key coverage exists for that dropbox.

An organisation using individual reviewer encryption can activate once its administrator exists; its first dropbox can't be published until an eligible recipient key exists and the recipient policy is valid. If policy mandates a recovery key, that must be satisfied before publishing an accepting dropbox.

Key onboarding checklist: approved recipient-key policy; required reviewer/team/organisation public keys; proof of possession or custody verification; key trust validation; recovery policy satisfied; encryption suite supported by public uploader clients. See [09-key-management-and-recipients.md](09-key-management-and-recipients.md).

## 11. Security-policy inheritance

Every new organisation inherits the current deployment baseline ([23-security-policy.md](23-security-policy.md)). Its initial policy version contains only explicit **strengthening** overrides. The effective policy fingerprint is recorded at activation. Onboarding parameters can never weaken the baseline.

## 12. Activation gates

Activation is a formal operation producing an **immutable activation report**. The activate command rechecks all critical gates at execution time; an earlier successful preflight is not sufficient.

| Gate | Requirement |
| --- | --- |
| Organisation identity | Valid immutable ID and unique slug |
| Approval | Required onboarding approvals satisfied |
| Database | Assigned and validated |
| Storage | Assigned and validated |
| Administrator | Verified, accepted membership, required MFA |
| Authentication | Trusted provider configuration valid |
| Security policy | Effective policy valid |
| Audit | Organisation audit context initialised |
| Retention | Required baseline policies assigned |
| Key governance | Organisation-level mandatory controls satisfied |
| Provisioning | No unresolved critical findings |

Organisation activation ≠ dropbox activation. Each dropbox must separately pass encryption, storage, retention and access-policy validation.

## 13. Self-service onboarding

Two modes:

- **Request-only** — applicant submits a request; an operator approves before provisioning.
- **Policy-controlled automatic** — eligible applicants provisioned with an operator-approved profile; requires explicit configuration and abuse controls.

Registration flow:

1. Applicant supplies organisation name and administrator email.
2. Rate limits and anti-abuse checks applied.
3. Applicant verifies control of the email.
4. Pending registration created/updated.
5. If approval required, enters review queue.
6. On approval, owner receives a short-lived enrolment invitation.
7. Owner establishes authentication and MFA.
8. Provisioning and security setup begin.
9. Readiness checks gate public intake.

Restrictions: no infrastructure selection; no platform role assignment; no custom secret-provider references; no unrestricted creation; verified contact method; clear pending/approved/rejected status; no exposure of internal provisioning details. No encryption keys or storage config requested before an authenticated admin session. Responses must not disclose whether an email already has an account (anti-enumeration). Self-service doesn't imply commercial or legal verification.

### Anti-abuse controls

- Rate limits by IP, email and registration fingerprint.
- Disposable-email detection as a configurable signal, not proof.
- Registration quotas and pending-application expiry.
- Email verification before allocating expensive resources.
- Optional CAPTCHA (or equivalent) after suspicious activity.
- Domain ownership checks where configured.
- Duplicate-registration detection with manual review.
- Delayed resource provisioning until approval.
- Restricted capabilities for newly activated self-service organisations.
- Minimise personal data; don't permanently store device fingerprints for anti-abuse; retain abuse data only as long as policy justifies.

## 14. API and CLI provisioning

API creation is idempotent: same `Idempotency-Key` + equivalent payload returns the original operation; same key + conflicting payload is rejected.

```http
POST /api/platform/organisations
Idempotency-Key: create-customer-123
Content-Type: application/json

{
  "displayName": "Example Organisation",
  "slug": "example-organisation",
  "databaseProfileId": "shared-postgres",
  "storageProfileId": "primary-s3",
  "initialAdministratorEmail": "admin@example.org"
}
```

CLI (uses the same authorisation and provisioning services; cannot bypass approval or isolation checks):

```bash
secureintake organisations create --name "Example Organisation" \
  --slug example-organisation --database-profile shared-postgres --storage-profile primary-s3
secureintake organisations provisioning status --organisation <id>
secureintake organisations activation-check --organisation <id>
secureintake organisations activate --organisation <id>
```

## 15. Suspension and reactivation

Reversible, distinct from deletion, privileged under v4.2, with a reason code and enforcement scope.

| Mode | Effect |
| --- | --- |
| Administrative | Blocks organisation administration except authorised recovery actions |
| Intake | Blocks new uploads and finalisation |
| Full operational | Blocks new intake and ordinary reviewer operations |
| Security lockdown | Restricts activity to explicitly authorised security/recovery operations; restricts new sessions and API credentials |

- Suspension never automatically deletes ciphertext, revokes keys, disables audit, or stops legally required retention.
- Already-issued presigned upload URLs may remain usable until expiry; **finalisation rechecks organisation state** and rejects unauthorised completion. **Exception: security lockdown** invalidates all sessions and upload sessions immediately. Presigned URLs can't be recalled, so finalisation is rejected and orphaned ciphertext cleaned up (Q-ONB-3, P-16).
- Reactivation re-runs readiness and security checks.

```csharp
public sealed record OrganisationSuspension(
    Guid OrganisationId, string Mode, string ReasonCode,
    DateTimeOffset EffectiveAt, Guid AuthorisedBy, Guid? ApprovedOperationId);
```

## 16. Deletion and offboarding

High-risk operation requiring independent approval. Never equivalent to deleting a row.

```text
Active/Suspended → Deletion requested → Retention and legal-hold assessment
→ Independent approval → Intake and access disabled → (export window for permitted metadata and ciphertext)
→ Eligible data deletion (durable jobs) → Storage reconciliation → Logical deletion completed
```

Must account for: submission ciphertext; encrypted form responses; server-readable metadata; receipt and evidence-package records; audit history; organisation configuration; storage objects and orphaned uploads; webhook/notification records; backup retention limitations.

- Cannot be physically erased while required evidence or legal holds remain.
- A **tombstone** preserves the minimum identifiers for audit, duplicate prevention and legal obligations.
- BYO storage: platform revokes its access and deletes where permitted, but cannot guarantee deletion of copies held by the storage owner.
- Private E2EE keys stay with their custodians; externally held copies can't be guaranteed destroyed.

## 17. Provisioning dashboard

Desktop-first. Shows per-gate status (identity, database, storage, initial administrator, security policy, audit context) and **actionable** failures — e.g. "Storage validation failed: write permission denied", not "Provisioning error".

## 18. Data model

`organisations` remains the authoritative lifecycle record. Additional tables:

```text
organisation_registrations              organisation_provisioning_requests
organisation_registration_verifications organisation_provisioning_operations
organisation_approval_decisions         organisation_provisioning_steps
organisation_invitations                organisation_provisioning_findings
organisation_onboarding_progress        organisation_resource_assignments
organisation_lifecycle_events           organisation_initial_administrator_invitations
organisation_external_references        organisation_activation_reports
organisation_suspensions                organisation_reactivation_operations
organisation_deletion_requests          organisation_deletion_operations
organisation_tombstones
```

(v1.8's `organisation_offboarding_requests` is superseded by `organisation_deletion_requests`.)

## 19. API surface

```text
POST /public/organisation-registrations
POST /public/organisation-registrations/verify-email
POST /public/invitations/accept                       # token in body, not path

GET  /api/platform/organisation-registrations
POST /api/platform/organisation-registrations/{id}/approve
POST /api/platform/organisation-registrations/{id}/reject

POST /api/platform/organisations
GET  /api/platform/organisations/{id}
GET  /api/platform/organisations/{id}/provisioning
POST /api/platform/organisations/{id}/activation-check
POST /api/platform/organisations/{id}/activate
POST /api/platform/organisations/{id}/suspend
POST /api/platform/organisations/{id}/reactivate

GET  /api/organisations/{id}/readiness
POST /api/organisations/{id}/invitations
POST /api/organisations/{id}/deletion-requests
```

## 20. Audit events

```text
organisation.provisioning.requested | approved | rejected | started | step_completed | failed
organisation.resource_assigned
organisation.initial_admin_invited | initial_admin_activated
organisation.activation_checked | activated
organisation.suspended | reactivated
organisation.deletion_requested | deletion_approved | deletion_started | deletion_completed
```

Include initiating method, actor/service identity, organisation ID, provisioning operation ID, policy fingerprint, approval references and outcome. Never include invitation tokens, infrastructure credentials or other secrets.

## 21. Implementation order

1. Unified provisioning service and lifecycle state machine.
2. Durable, idempotent provisioning steps.
3. Database/storage profile assignment and validation.
4. Initial administrator invitation and MFA bootstrap.
5. Security-policy inheritance and activation gates.
6. Manual administration and CLI provisioning.
7. Scoped API provisioning.
8. Configurable self-service onboarding (email verification, optional approval, anti-abuse, registration quotas).
9. Suspension and reactivation.
10. Deletion/offboarding workflows and audit.
11. Domain and advanced business verification (optional).
