# Access Control, JIT Elevation and Privileged Operations

Source: notes v4.2. Builds on a role model and approval engine defined earlier (v2.8 approvals; base roles from versions not in the notes). Resolved questions: [QUESTIONS.md](../QUESTIONS.md) (Q-ACC-*). Planning decisions applied below take precedence over the source notes.

> **Planning decisions applied (2026-11-03)** — these override the text below where they differ. See [QUESTIONS.md](../QUESTIONS.md).
> - Permission catalogue: [28](28-permission-catalogue.md) (Q-ACC-1). Roles are complete, plus a **Provisioning Client** platform role (Q-ACC-2).
> - Break-glass: FIDO2 emergency credentials (Q-ACC-3, see [18](18-backup-and-recovery.md)). v1.0 has **built-in identity and OIDC** (Q-ACC-4).

## Decision

**RBAC + just-in-time (JIT) privilege elevation + independent approval for sensitive operations.** No mandatory external PAM system.

> A permanent role establishes what an administrator may **request**. A temporary elevation grant establishes what they may **perform**. An operation-specific approval establishes whether a sensitive action may **proceed**. Three separate authorisation decisions.

A JIT grant never approves an operation; an approval never grants permission to execute.

## 1. Flow

```text
Authenticated administrator (OIDC or built-in · base roles · session)
→ Request elevation (permission · organisation/resource scope · reason · duration)
→ Fresh MFA + eligibility (base role · session assurance · risk policy · revocation)
→ JIT grant (temporary, scoped)
→ Independent approval (sensitive operations only)
→ Authorisation at execution (role + grant + approval + current policy + immutable target)
→ Execute and audit (durable decision · fenced execution · outcome · signed audit checkpoint)
```

## 2. Roles

**Platform roles**

| Role | Responsibility |
| --- | --- |
| Owner | Deployment governance and ownership |
| Admin | Platform administration |
| Security Admin | Security policies, key governance, privileged access |
| Operator | Service operations and diagnostics |
| Auditor | Read-only audit and compliance |

**Organisation roles**

| Role | Responsibility |
| --- | --- |
| Owner | Organisation governance |
| Admin | Organisation administration |
| Dropbox Manager | Dropbox and form configuration |
| Reviewer | Submission review |
| Security Officer | Security and sensitive-operation approval |
| Auditor | Read-only evidence and audit |
| API Client | Scoped machine operations |

Multiple memberships are supported; permissions are evaluated within the correct scope. **No platform role (including Owner/Operator) grants decryption of organisation submissions or cryptographic access to E2EE content.**

### Permission levels

| Level | Examples | Requirement |
| --- | --- | --- |
| Standard | View dashboard, manage assigned submissions | Base role |
| Elevated | Retry jobs, export diagnostics, change integrations | Base role + JIT grant |
| Sensitive | Change trust anchors, bulk export, activate recovery | Base role + JIT + independent approval (sometimes ≥2 approvers) |

## 3. JIT grants

Narrow, temporary, revocable:

```json
{
  "grantId": "elev_01J...", "principalId": "usr_123", "organisationId": "org_456",
  "permissions": ["operations.jobs.manage"],
  "resourceScope": { "type": "job_category", "id": "webhook.delivery" },
  "reason": "Investigating failed webhook deliveries",
  "issuedAt": "2026-11-03T08:00:00Z", "expiresAt": "2026-11-03T08:30:00Z", "status": "active"
}
```

No wildcard all-organisation scope unless the operation genuinely needs deployment-wide scope and policy allows it.

Proposed configurable maximum durations:

| Elevation | Max |
| --- | --- |
| Operational job management | 30 min |
| Integration configuration | 15 min |
| Sensitive evidence export | 10 min |
| Security configuration | 10 min |
| Recovery activation | Single operation |
| Break-glass | 15 min, renewable only under policy |

Grants are never silently extended by UI activity.

Lifecycle: `Requested → Eligibility checked → MFA required → Active → Expired | Revoked | Consumed`.

Request modes: `automatic_after_mfa` (low risk), `approval_required` (higher risk), `operation_bound` (one specific sensitive action). A reason is mandatory and audited, but free text is never an authorisation rule.

The effective grant never exceeds the principal's eligible role permissions and applicable security policy.

## 4. MFA

| Method | Privileged use |
| --- | --- |
| WebAuthn/FIDO2 security key | Preferred |
| Platform passkey | Preferred where assurance policy permits |
| OIDC with verified MFA assurance | Supported |
| TOTP | Fallback where allowed |
| Email OTP / SMS OTP | Not sufficient for high-risk elevation |

For OIDC, successful login ≠ MFA. Evaluate trusted issuer config, `acr`, `amr` (where reliable) and `auth_time`.

```json
{ "privilegedAuthentication": { "maxAuthenticationAgeSeconds": 300,
    "requirePhishingResistantMfa": true, "allowTotpFallback": false } }
```

The authentication timestamp comes from a trusted authentication event, never the client.

## 5. Privileged sessions

The normal session (identity, base roles, memberships, expiry) is separate from the **elevation context** (grant ID, scope, MFA assurance, issued time, expiry, revocation state). Users stay signed in after elevation expires.

- Browser: server-side privileged session reference bound to the authenticated session. **No long-lived privileged bearer tokens in `localStorage`.** Secure, HttpOnly, SameSite cookies with CSRF protection.
- Elevation is revoked on: sign-out; base session revocation; account suspension; role removal; MFA assurance invalidation; expiry; explicit revocation; deployment security state requiring privilege reset.
- Sensitive operations check revocation against authoritative state.

## 6. Approval-controlled operations

Extends the v2.8 approval engine ([10](10-review-workflows.md)) — no second approval system. Each sensitive operation creates an immutable request:

```json
{ "operationId": "op_123", "operationType": "evidence.export.bulk", "requestedBy": "usr_123",
  "organisationId": "org_456", "targetManifestDigest": "sha256:...", "parametersDigest": "sha256:...",
  "requiredApprovals": 2, "expiresAt": "2026-11-03T09:00:00Z" }
```

Approvers see the real operation details; the digests bind approval to the exact target and parameters.

Rules:

- No self-approval; each approval from a distinct authorised human with the right role and fresh MFA.
- Approvals are non-transferable; material parameter changes invalidate them; expiry enforced; revocation checked before execution.
- Approval never bypasses legal holds, tenant boundaries or cryptographic restrictions.
- Quorum policies define exact roles and number of approvers.

## 7. Enforcement at execution

Checked immediately before execution (not just at request time):

```text
Authenticated principal AND required base permission AND active scoped JIT grant
AND valid operation-specific approvals AND unchanged target manifest
AND current security policy permits AND no active revocation or blocking hold
```

- Async workers receive an **immutable operation reference** (never `approved: true`) and re-evaluate before irreversible steps.
- Coordinate authorisation and claiming transactionally where possible; long-running operations use durable execution leases and fencing tokens. Cancellation/revocation stops later protected steps; completed external actions may not be reversible.

```csharp
public interface IPrivilegedOperationAuthorizer
{
    ValueTask<PrivilegedAuthorizationResult> AuthorizeAsync(PrivilegedOperationContext context, CancellationToken ct = default);
}

public sealed record PrivilegedOperationContext(
    Guid PrincipalId, Guid SessionId, Guid? OrganisationId, string Permission,
    Guid? ElevationGrantId, Guid? ApprovedOperationId, string TargetManifestDigest);

public sealed record PrivilegedAuthorizationResult(
    bool Allowed, string DecisionCode, Guid? ElevationGrantId, Guid? ApprovedOperationId);

public sealed class ExecuteSensitiveOperationHandler(
    IPrivilegedOperationAuthorizer authorizer, ISensitiveOperationRepository repository)
{
    public async Task ExecuteAsync(SensitiveOperation command, CancellationToken ct)
    {
        var decision = await authorizer.AuthorizeAsync(command.AuthorizationContext, ct);
        if (!decision.Allowed) throw new UnauthorizedAccessException(decision.DecisionCode);
        await repository.ExecuteWithFencingAsync(command, ct);   // still enforces concurrency + immutable target
    }
}
```

Uses authoritative DB state, not client role claims. High-risk operations go through a central command handler.

## 8. Emergency (break-glass) access

For IdP outage, loss of all platform admins, critical config failure, or post-DR activation. **Not** a hidden permanent superuser with a default password.

- Operator-provisioned **offline recovery credential** with strong custody; hardened deployments use multiple custodians / threshold approval where feasible.
- Authenticates to a restricted recovery interface or CLI only.

| Permitted | Prohibited |
| --- | --- |
| Restore admin IdP configuration | Decrypting E2EE submissions without recipient keys |
| Inspect deployment health | Changing original receipts |
| Validate recovery state | Disabling audit |
| Activate a recovered deployment | Bypassing tenant isolation |
| Rotate compromised platform credentials | Deleting evidence without retention checks |
| Restore trusted signing-key configuration | |

```text
Emergency declared → Recovery credential presented → Credential and custody policy verified
→ Restricted emergency session → Permitted actions → Session terminated → Mandatory post-incident review
```

Emergency actions are audited and trigger security notifications via independent channels. If the primary DB is down, the recovery CLI keeps a protected append-only **local emergency journal**, reconciled into audit after recovery (integrity/custody limits documented).

## 9. API tokens and machine identities

| Token | Purpose |
| --- | --- |
| User access token | Normal authenticated API access |
| Privileged user session | Short-lived JIT operations |
| Service account token | Scoped automation |
| Operation capability | One approved action |
| Emergency credential | Restricted recovery access |

- No long-lived tokens inheriting a human admin's full permissions.
- Service accounts have explicit owners, scopes, expiry and rotation; they can't request human-style JIT unless a dedicated machine-operation policy allows it.
- Sensitive automation uses pre-approved, policy-bound capabilities (e.g. scheduled evidence export limited by scope, destination, volume). A service account can't approve its own unrestricted exports.

Token security: OAuth2 client credentials where an IdP is configured; built-in tokens are opaque, high-entropy, stored as keyed hashes. Short expiry, narrow scopes, organisation binding, optional mTLS/proof-of-possession, overlapping rotation, immediate revocation, last-used metadata, rate limits, never in logs or query strings. Resource servers also evaluate current service-account status, organisation state and policy — claimed scope isn't enough.

## 10. Admin UI

- Ordinary pages work without repeated prompts; elevation is requested only when an action needs it, showing action, scope, duration, required authentication and consequences (e.g. "duplicate external deliveries may occur").
- Active elevation scope and remaining time are always visible; one grant must not look like unrestricted admin.
- **Active Elevations** page: users end their own; Security Admins revoke within scope.
- **Desktop-only:** security policy changes; trusted issuer/key management; bulk evidence exports; native evidence restoration; recovery activation; break-glass administration; high-risk job replay; privileged service-account creation. Mobile: basic monitoring, alert acknowledgement, routine review.
- The API enforces the same permissions regardless of client — desktop-only is risk reduction, not an authorisation boundary.

## 11. API

```text
POST   /api/admin/elevations
GET    /api/admin/elevations
DELETE /api/admin/elevations/{grantId}
POST   /api/admin/operations
GET    /api/admin/operations/{operationId}
POST   /api/admin/operations/{operationId}/approve
POST   /api/admin/operations/{operationId}/reject
POST   /api/admin/operations/{operationId}/execute
GET    /api/admin/privileged-sessions
DELETE /api/admin/privileged-sessions/{sessionId}
```

Organisation-scoped equivalents follow the organisation route structure. State-changing endpoints: CSRF protection (cookie auth), idempotency where appropriate, strict authorisation.

## 12. Data model

```text
privileged_elevation_requests     privileged_session_events
privileged_elevation_grants       emergency_access_credentials
privileged_elevation_revocations  emergency_access_sessions
privileged_operation_requests     emergency_access_reviews
privileged_operation_approvals    service_accounts
privileged_operation_executions   service_account_credentials
privileged_session_assurance      service_account_permission_grants
```

Generalise the v2.8 approval tables rather than duplicate. Grant columns: `grant_id, principal_id, session_id, organisation_id, permission, resource_scope, issued_at, expires_at, revoked_at, mfa_event_id, policy_version, reason`.

## 13. Audit events

```text
privilege.elevation.requested | granted | denied | expired | revoked
privilege.operation.requested | approved | rejected | executed | failed
privilege.session.created | terminated
privilege.emergency.activated | action_executed | terminated | review_completed
service_account.created | permission_changed | credential_rotated | revoked
```

Capture actor, scope, policy version, MFA assurance, approval references, target digest, outcome. Never raw assertions, passkey secrets, tokens or recovery credentials.

## 14. Security invariants (automated tests required)

1. A base role alone cannot execute an elevation-required operation.
2. An expired or revoked elevation cannot authorise a new action.
3. A user cannot approve their own sensitive operation.
4. An approval cannot be reused for different parameters.
5. No elevation into another organisation without eligible membership and explicit scope.
6. A service account can't bypass human approval policy by holding a token.
7. Break-glass can't disable mandatory audit or decrypt E2EE content.
8. A privileged browser session can't be silently extended by normal activity.
9. Worker execution rechecks authorisation against the immutable operation.
10. Tenant isolation and cryptographic boundaries hold regardless of administrative privilege.

## 15. Implementation order

1. Permission classification and privileged-operation catalogue.
2. Fresh MFA assurance validation.
3. Session-bound JIT grants.
4. Central privileged authorisation service.
5. Integration with the approval engine.
6. Immutable operation manifests and execution fencing.
7. Privileged admin UI.
8. Service-account and API-token restrictions.
9. Emergency-access CLI and recovery journal.
10. Audit, security alerts and adversarial testing.
