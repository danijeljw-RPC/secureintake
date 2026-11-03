# Hierarchical Security Policy

Source: notes v4.3. Extends [22-access-control.md](22-access-control.md). Resolved questions: [QUESTIONS.md](../QUESTIONS.md) (Q-POL-*). Planning decisions applied below take precedence over the source notes.

> **Planning decisions applied (2026-11-03)** — these override the text below where they differ. See [QUESTIONS.md](../QUESTIONS.md).
> - Session, MFA and approval defaults are in [27](27-production-defaults.md) (Q-POL-1).
> - Lockout exceptions need a platform Owner and a Security Admin. They're time-boxed to 90 days and stored as `policy_exceptions` (Q-POL-2).

## Decision

**Hierarchical security policies:** deployment-level baselines plus organisation-level overrides that may **only strengthen** security. No general-purpose policy scripting engine.

> An organisation may impose stricter controls than the deployment baseline, but it cannot weaken, disable or bypass a deployment requirement.

Evaluation is server-side and consistent across API nodes, workers, CLI and admin UIs. Every execution is evaluated against the **current** effective policy — not the policy in force at sign-in or approval request.

## 1. Hierarchy and ownership

```text
Deployment security baseline   mandatory · operator-owned · cannot be weakened
        +
Organisation security policy   strengthening-only overrides · organisation-owned
        +
Resource-specific restrictions dropbox · evidence export · recovery · privileged operation
        =
Effective security policy      resolved · validated · versioned · enforced
```

| Layer | Owner | May weaken parent? |
| --- | --- | --- |
| Deployment baseline | Platform operator | n/a |
| Organisation policy | Organisation security administrator | No |
| Resource restrictions | Resource/security administrator | No |
| Operation-specific approval | Independent approvers | No |

The resource layer only contains controls explicitly designated resource-scoped — it's not a fourth configuration system. A structurally valid override is never accepted on that basis alone.

## 2. Policy catalogue (initial, deliberately limited)

| Category | Example controls |
| --- | --- |
| Authentication | MFA assurance, freshness, permitted factors |
| Sessions | Max lifetime, idle timeout, elevation duration |
| Privileged access | Eligible permissions, elevation requirements |
| Approvals | Quorum, approver roles, independence, expiry |
| Evidence | Export restrictions, import/restoration approvals |
| Key governance | Rotation approval, recovery controls |
| Operations | Job replay, diagnostics export, recovery activation |
| Audit | Required event coverage, checkpoint frequency |
| Access restrictions | Allowed client types, admin UI restrictions |

Tenant retention and legal holds stay in their own policy systems; they can add restrictions but aren't merged into access policy.

## 3. Strengthening-only semantics

Each field has an explicit comparison strategy (not a blanket `max()`/`min()`):

| Policy type | Stronger means | Example |
| --- | --- | --- |
| Minimum requirement | Higher | MFA assurance level |
| Maximum allowance | Lower | Elevation lifetime |
| Allowed set | Smaller subset | Permitted factors |
| Required set | Larger superset | Mandatory approval roles |
| Boolean requirement | `true` dominates | Fresh MFA required |
| Boolean permission | `false` dominates | Bulk export permitted |
| Approval quorum | Higher, subject to feasibility | 2 approvals instead of 1 |
| Exact constraint | Explicit compatibility rule | Trusted issuer policy |

Examples: deployment max elevation 30 min + organisation 15 min → 15 min. Deployment requires fresh MFA + organisation `false` → **invalid override**.

A stronger policy must be at least as restrictive for **every** relevant decision. If the engine can't prove an override is strengthening-only, it rejects it.

## 4. Schema

Typed, versioned overlay; omission means inherit:

```json
{
  "policySchemaVersion": 1,
  "policyId": "policy_org_123",
  "scope": { "type": "organisation", "id": "org_123" },
  "authentication": {
    "requireFreshMfaForElevation": true,
    "maxMfaAgeSeconds": 180,
    "allowedPrivilegedFactors": ["fido2_security_key", "platform_passkey"]
  },
  "privilegedAccess": { "maxElevationSeconds": 900, "allowBulkEvidenceExport": false },
  "approvals": { "minimumIndependentApprovers": 2, "maxApprovalAgeSeconds": 600 }
}
```

Explicit `null` on security-critical fields is rejected unless the schema defines it. Unknown fields are rejected.

## 5. Resolution

Typed composition (not JSON merging) producing a complete effective policy with a fingerprint and all contributing versions, so audit records identify the exact policy behind each decision.

```csharp
public interface ISecurityPolicyResolver
{
    ValueTask<EffectiveSecurityPolicy> ResolveAsync(SecurityPolicyContext context, CancellationToken ct = default);
}

public sealed record SecurityPolicyContext(Guid? OrganisationId, string Operation, string? ResourceType, Guid? ResourceId);

public sealed record EffectiveSecurityPolicy(
    string Fingerprint, long DeploymentVersion, long? OrganisationVersion, long? ResourceVersion,
    AuthenticationPolicy Authentication, PrivilegedAccessPolicy PrivilegedAccess, ApprovalPolicy Approvals);
```

## 6. Conflict resolution

- **Valid strengthening:** deployment ≥1 approver, organisation ≥2 → 2.
- **Invalid weakening:** deployment requires phishing-resistant MFA, organisation allows TOTP → reject.
- **Incompatible:** deployment requires Security Officer approvers, organisation requires Owners only → reject as unsatisfiable unless a defined intersection/combined-role interpretation yields eligible approvers. Never silently pick one.

**Unsatisfiable but secure** policies (e.g. three Security Officers required, one exists) may be saved as drafts with findings but **publication is blocked by default**. Deliberate lockout policies need a separate platform-governed exception process.

## 7. MFA assurance levels

| Level | Requirement |
| --- | --- |
| Standard | Authenticated session |
| Elevated | Recent MFA from an accepted factor |
| Phishing-resistant | Verified WebAuthn/FIDO2 or trusted equivalent |
| Recovery | Restricted emergency procedure — a separate mode, **not** a stronger level |

An emergency credential never satisfies ordinary privileged MFA.

```json
{ "authentication": { "minimumPrivilegedAssurance": "phishing_resistant", "maxMfaAgeSeconds": 300,
    "maxSessionIdleSeconds": 1800, "maxSessionAbsoluteSeconds": 28800 } }
```

Organisations may shorten (e.g. MFA age 120 s, idle 900 s) but never lengthen.

## 8. Approval policy

```csharp
public sealed record ApprovalPolicy(
    int MinimumIndependentApprovers, TimeSpan MaximumApprovalAge,
    bool ProhibitSelfApproval, bool RequireFreshMfa,
    IReadOnlySet<string> RequiredApproverRoles,
    bool RequireDistinctPrincipals, bool RequireDistinctApprovalSessions);
```

Operation-specific examples:

| Operation | Deployment minimum | Organisation strengthening |
| --- | --- | --- |
| Retry ordinary job | JIT elevation | Require approval |
| Export one evidence package | JIT + 1 approver | Require 2 |
| Bulk evidence export | JIT + 2 approvers | Disable entirely |
| Change signing-key trust | JIT + 2 approvers | Require Security Officer **and** Owner |
| Activate recovery | Restricted recovery procedure + approval | Additional independent custodian |

Role requirements must state whether roles are **alternatives** or **separate mandatory slots** — merging role names into one set is insufficient.

Approvals bind to: immutable target manifest; operation; effective policy fingerprint; approval-policy version; approver identity and assurance; expiry; quorum and role slots. If the effective policy changes before execution, approvals are re-evaluated; they remain usable only if they satisfy the new policy and the target hasn't changed. **A policy change never grandfathers an operation into weaker authorisation.**

## 9. Lifecycle and versioning

```text
Draft → Validated → Pending approval → Published → Active → Superseded   (also Rejected)
```

Published versions are immutable; any change is a new draft and version.

```csharp
public sealed record SecurityPolicyVersion(
    Guid PolicyId, long Version, SecurityPolicyStatus Status, string DocumentDigest,
    string SchemaVersion, Guid CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset? ActivatedAt);
```

The fingerprint covers the canonical **effective** policy — a baseline change alters an organisation's effective policy even if its overlay is unchanged.

## 10. Baseline changes

Impact analysis before activation: organisations whose effective policy changes; grants that become invalid; pending approvals needing re-evaluation; affected service accounts; sessions needing fresh MFA; organisations lacking eligible approvers; newly prohibited operation types.

A stricter baseline takes effect **immediately** at the authoritative boundary once activated; existing sessions and grants lose newly prohibited permissions. Distributed activation uses a monotonic generation in a shared authoritative record; nodes that can't confirm a compatible generation **fail closed** for protected operations.

## 11. Simulation

Admin UI and CLI simulator (structured decision trace with safe reason codes and effective versions; never issues grants or executes):

```text
POST /api/platform/security-policies/simulate
POST /api/organisations/{id}/security-policies/simulate
```

## 12. Administration permissions

| Permission | Purpose |
| --- | --- |
| `security.policy.view` | Read effective policy |
| `security.policy.draft` | Create/edit drafts |
| `security.policy.simulate` | Run simulations |
| `security.policy.submit` | Submit for publication |
| `security.policy.approve` | Independently approve publication |
| `security.policy.publish` | Activate an approved version |
| `security.policy.audit` | Review history |

Organisation admins can view the baseline applicable to them without seeing sensitive platform infrastructure. Publishing is a sensitive operation under v4.2; no self-approval.

## 13. Rollout

- **Immediate** — urgent security changes (revoked auth methods, newly prohibited operations).
- **Scheduled** — planned changes affecting admins, approval staffing or workflows.

A durable activation record and worker suffice (no rollout orchestrator). Safeguards: validate → prove strengthening-only → simulate affected operations → identify lockout risk → collect approvals → commit new generation → invalidate incompatible grants and cached decisions → record activation and audit.

Caches: short-lived or generation-aware for ordinary evaluation; authoritative checks for high-risk operations.

## 14. Enforcement integration

```csharp
public interface ISecurityPolicyEvaluator
{
    ValueTask<PolicyDecision> EvaluateAsync(PolicyEvaluationRequest request, CancellationToken ct = default);
}

public sealed record PolicyEvaluationRequest(
    Guid PrincipalId, Guid SessionId, Guid? OrganisationId, string Operation,
    string? ResourceType, Guid? ResourceId, string? TargetManifestDigest);

public sealed record PolicyDecision(
    bool Allowed, string DecisionCode, string EffectivePolicyFingerprint, IReadOnlyList<string> Requirements);
```

Called by the v4.2 privileged-operation authoriser. Doesn't replace RBAC, tenant isolation, legal-hold checks or recipient validation — the result is the **intersection** of all controls. No expression languages, embedded JavaScript or user SQL initially (typed catalogue is easier to validate, test, audit and keep AOT-compatible).

## 15. Lockout recovery

Uses the restricted break-glass procedure ([22](22-access-control.md) §8). Never disables the baseline. Limited to recovering admin access, correcting an invalid activation, or restoring an approved prior configuration; preserves the original version, records the correction, requires post-incident review. Rolling back to a policy that violates the current baseline isn't permitted.

## 16. Data model

```text
security_policy_definitions          security_policy_simulation_runs
security_policy_versions             security_policy_approval_requests
security_policy_activation_records   security_policy_impact_assessments
security_policy_effective_snapshots  security_policy_rollback_events
security_policy_validation_runs
```

Effective snapshots may be materialised for performance; published documents and activation history remain authoritative. Each version keeps its canonical digest and schema version.

## 17. Audit events

```text
security.policy.draft_created | draft_updated
security.policy.validation_completed | validation_failed
security.policy.simulated
security.policy.publication_requested | publication_approved | publication_rejected
security.policy.activated | superseded
security.policy.rollback_requested | rollback_completed
security.policy.override_rejected | conflict_detected | effective_policy_changed
```

Record actor and scope, previous/new version, canonical digest, effective fingerprint, approvals, validation and impact results, activation time and generation. No secrets or sensitive user data.

## 18. Implementation order

1. Typed catalogue and restriction-comparison rules.
2. Deployment baseline resolver.
3. Organisation strengthening-only overrides.
4. Effective fingerprinting and immutable versions.
5. MFA and approval-policy composition.
6. Integration with v4.2 privileged authorisation.
7. Validation and simulation.
8. Publication approvals and impact analysis.
9. Distributed activation and cache invalidation.
10. Admin UI, recovery safeguards and audit.
