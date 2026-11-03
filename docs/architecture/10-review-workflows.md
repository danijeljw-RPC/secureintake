# Submission Review Workflows

Source: notes v2.8. Resolved questions: [QUESTIONS.md](../QUESTIONS.md) (Q-WF-*). Planning decisions applied below take precedence over the source notes.

> **Planning decisions applied (2026-11-03)** — these override the text below where they differ. See [QUESTIONS.md](../QUESTIONS.md).
> - v1.0 ships the **default workflow** only. Custom workflows and automation are v1.1.
> - Internal notes are encrypted to the submission's current recipients plus the recovery recipient (Q-WF-1).
> - `create_internal_task` is removed (Q-WF-2). Uploaders never see outcomes: Received → In review → Completed, plus optional 'More information requested' (Q-WF-3).

## Decision

Organisations can define their own **review workflows**: stages, assignment rules, approvals, internal notes, escalations and automation. Implemented as a **versioned workflow engine inside the modular monolith**, not rules scattered through the submission API. Optional for API consumers — organisations may integrate an external system via APIs and webhooks instead.

The platform stays **inbound-only** — not a document exchange or messaging platform. The engine **never decrypts files or needs plaintext**; decisions use submission metadata, authorised reviewer actions and explicitly supplied workflow information.

> Workflow state, reviewer assignment, ciphertext retrieval permission and cryptographic access are separate concerns. A workflow can decide who should process a submission; it can't grant cryptographic access without a recipient key or authorised client-side rewrap.

## 1. Architecture

```text
Encrypted submission received (submission + ciphertext committed)
  → Workflow instance (versioned definition · current state · assignment · due dates)
       ├── Assignment (reviewer or team)
       ├── Approvals (human decisions)
       └── Escalations (deadlines and alerts)
  → Durable transition + audit + outbox (consistent state, at-least-once notifications)
```

## 2. States

Default template (organisations may add/remove/rename internal stages within platform constraints):

`Received → Awaiting Assignment → In Review → More Information Required → Pending Approval → Completed → Closed`

```json
{ "key": "pending_approval", "displayName": "Pending Approval", "category": "active",
  "terminal": false, "publicStatus": "in_review", "requiresAssignment": true }
```

State keys are immutable within a version; display names may be localised.

## 3. Definitions and versioning

Published definitions are **immutable**. New submissions use the current published version; existing submissions stay on their starting version unless explicitly migrated.

```json
{
  "key": "document_review", "version": 3, "initialState": "received",
  "states": [
    { "key": "received", "category": "active", "publicStatus": "received" },
    { "key": "in_review", "category": "active", "publicStatus": "in_review" },
    { "key": "pending_approval", "category": "active", "publicStatus": "in_review" },
    { "key": "completed", "category": "terminal", "publicStatus": "completed" }
  ],
  "transitions": [
    { "key": "start_review", "from": "received", "to": "in_review", "requiredPermission": "submissions.review" },
    { "key": "request_approval", "from": "in_review", "to": "pending_approval", "requiredPermission": "submissions.request_approval" }
  ]
}
```

Validated against a published JSON Schema. **No executable code in definitions.** Publishing validates that every reachable non-terminal state can reach completion/closure, unless it's an intentional waiting state with an escalation or admin escape route.

## 4. Transition engine

All transitions go through one service:

```csharp
public interface IWorkflowTransitionService
{
    Task<WorkflowTransitionResult> ExecuteAsync(WorkflowTransitionRequest request, CancellationToken ct = default);
}
public sealed record WorkflowTransitionRequest(
    Guid OrganisationId, Guid SubmissionId, string TransitionKey, Guid ActorId,
    long ExpectedVersion, string? Reason, string IdempotencyKey);
public sealed record WorkflowTransitionResult(Guid WorkflowInstanceId, string PreviousState, string CurrentState, long Version);
```

1. Resolve tenant and instance. 2. Validate expected version (**optimistic concurrency**). 3. Verify actor permission and guards. 4. Check approvals, holds and constraints. 5. Commit state + audit. 6. Write notification/automation events to the outbox. Idempotency keys make retries safe.

The API exposes the **permitted transitions for the current actor** so clients don't duplicate authorisation logic.

## 5. Assignment rules

| Rule | Behaviour |
| --- | --- |
| Fixed team | Assign to a configured team |
| Fixed reviewer | Assign to a designated reviewer |
| Round robin | Distribute across eligible reviewers |
| Least active | Prefer fewer active assignments |
| Metadata-based | Match on approved server-readable metadata |
| Manual | Unassigned queue |
| Escalation assignment | Reassign on deadline/condition |

```json
{ "key": "finance_assignment", "trigger": "submission.received", "strategy": "least_active",
  "eligibleTeamId": "team_finance", "fallback": "manual_queue" }
```

Rules use only server-available metadata (file contents are encrypted). Round-robin/least-active use transactional coordination. Eligibility requires access **and** recipient-key policy, not just an active account.

### Assignment vs decryption

Per assignment, compute workflow eligibility, retrieval permission, available envelopes, rewrap need and recovery need — exposed as **recipient coverage**:

```json
{ "submissionId": "sub_123", "assignedReviewerId": "usr_456", "retrievalAuthorised": true,
  "recipientEnvelopeAvailable": false, "cryptographicActionRequired": "rewrap" }
```

The server knows envelope availability, not whether the reviewer still holds the key. Assignments needing rewrap may be **pending cryptographic access** ([09](09-key-management-and-recipients.md)).

## 6. Approvals

- **Business review approval** — organisation-defined (accept a submission, complete a review).
- **Security-sensitive approval** — mandatory controlled operations (recovery-key use, deletion, legal-hold release) under [22](22-access-control.md).

Shared infrastructure; organisations can configure business approvals but never bypass mandatory security approvals.

| Rule | Example |
| --- | --- |
| Single approver | One authorised manager |
| Multiple approvers | Two independent reviewers |
| Role-based | Organisation Security Officer |
| Team-based | Any eligible member of a team |
| Sequential | Reviewer, then manager |
| Unanimous | All specified approvers |
| Quorum | Two of three |

```json
{ "approvalId": "apr_123", "workflowInstanceId": "wf_456", "policyVersion": 2, "requiredApprovals": 2,
  "allowSelfApproval": false, "status": "pending", "expiresAt": "2026-11-05T12:00:00Z" }
```

Policies state whether self-approval is allowed (always prohibited for sensitive operations). Permissions and state are revalidated before applying the final decision.

## 7. Internal notes

Visible to authorised reviewers/admins; **never** in public status responses. Plain text or limited Markdown; author and timestamp; immutable revisions; role- and access-restricted; audited; optional mentions of authorised reviewers.

Encryption models:

1. Server-readable (DB encryption + RBAC).
2. **E2EE notes — recommended default**; server-readable is an explicitly declared lower-confidentiality option.

E2EE notes lose server-side search, content-based automation and moderation; metadata (author, timestamp, count) remains usable.

**Append-only revisions** (`created → corrected → redacted`); auditors can inspect permitted history. Note revisions fall under retention and legal holds. Redaction isn't claimed as cryptographic erasure if earlier ciphertext/keys remain.

## 8. Escalations

Durable background jobs. Triggers: time since submission; time in state; time since assignment; approval deadline; reviewer inactivity; repeated transition failures; missing cryptographic access; manual escalation.

```json
{ "key": "review_overdue",
  "trigger": { "type": "state_duration", "state": "in_review", "durationHours": 48 },
  "actions": [ { "type": "notify", "recipient": "team_manager" }, { "type": "set_priority", "value": "high" } ] }
```

Deduplication and cooldowns; failed notifications don't create duplicate escalation records.

## 9. Public status mapping

Fixed public vocabulary (organisations can't invent public codes — preserves API compatibility and avoids disclosing internal process):

| Public status | Meaning |
| --- | --- |
| Received | Submission accepted |
| In review | Organisation is processing it |
| More information required | Additional inbound information requested |
| Completed | Review completed |
| Closed | Workflow closed |

| Internal state | Public status |
| --- | --- |
| Awaiting Assignment | Received |
| Assigned, Pending Manager Approval, Legal Review | In review |
| Awaiting Additional Documents | More information required |
| Approved, Rejected | Completed |
| Archived | Closed |

Public responses never reveal reviewer identities, escalation details, security flags or notes. Public notifications fire only on effective public status change ([05](05-notifications.md)).

## 10. More-information requests

Not two-way messaging. A controlled request lets the uploader add a **submission revision** via the scoped return-access mechanism after required verification:

```json
{ "requestId": "req_123", "submissionId": "sub_456", "requestType": "additional_files",
  "messageTemplate": "additional_documents_required", "expiresAt": "2026-11-10T12:00:00Z" }
```

The organisation never sends files back. Free-form communication stays outside the platform unless a separate product decision adds it.

## 11. Automation

Constrained rules engine (no scripts).

Triggers: `submission.received`, `submission.revision.added`, `submission.assigned`, `workflow.state.changed`, `workflow.approval.completed`, `workflow.deadline.reached`, `workflow.escalation.triggered`.

Actions: `assign_team`, `assign_reviewer`, `set_priority`, `set_due_date`, `request_approval`, `transition_state`, `send_notification`, `emit_webhook`, `create_internal_task`.

```json
{ "name": "Auto-assign new submissions", "trigger": "submission.received",
  "conditions": [ { "field": "dropbox.category", "operator": "equals", "value": "finance" } ],
  "actions": [ { "type": "assign_team", "teamId": "team_finance" }, { "type": "set_priority", "value": "normal" } ] }
```

Allowlisted fields and operators only ([11](11-forms-and-templates.md) — only approved server-readable fields). No SQL, JavaScript, C# expressions or HTTP calls; external integrations use webhooks. No rule can decrypt files or export keys.

**Safety:** max automation depth; per-event execution limits; idempotency; loop detection; cooldowns; explicit transition permissions; durable execution history; dead-letter handling; tenant quotas. Automation can *request* approval but never manufacture the decision.

## 12. Administration

Desktop-first workflow editor: draft/published versions; states and transitions; assignment rules; approval policies; escalation rules; public status mapping; validation and simulation; audit history.

## 13. Data model

```text
workflow_definitions            workflow_approval_decisions
workflow_definition_versions    submission_internal_notes
workflow_states                 submission_internal_note_revisions
workflow_transitions            workflow_escalation_policies
workflow_instances              workflow_escalation_events
workflow_transition_history     workflow_automation_rules
workflow_assignment_rules       workflow_automation_executions
workflow_approval_policies      workflow_deadlines
workflow_approval_requests
```

Tenant-scoped; for dedicated tenants these live in the tenant data plane.

## 14. API

```text
GET  /api/organisations/{id}/workflows
POST /api/organisations/{id}/workflows
GET  /api/organisations/{id}/workflows/{workflowId}
POST /api/organisations/{id}/workflows/{workflowId}/publish
GET  /api/organisations/{id}/submissions/{submissionId}/workflow
POST /api/organisations/{id}/submissions/{submissionId}/transitions
GET  /api/organisations/{id}/submissions/{submissionId}/notes
POST /api/organisations/{id}/submissions/{submissionId}/notes
GET  /api/organisations/{id}/workflow-approvals
POST /api/organisations/{id}/workflow-approvals/{approvalId}/approve
POST /api/organisations/{id}/workflow-approvals/{approvalId}/reject
GET  /api/organisations/{id}/workflow-automations
POST /api/organisations/{id}/workflow-automations
```

## 15. Implementation order

1. Definition schema and validator.
2. Versioned publishing.
3. Instances and transition engine.
4. Default review workflow.
5. Team and reviewer assignment integration.
6. Internal notes.
7. Approval stages.
8. Deadline and escalation workers.
9. Public status mapping.
10. Constrained automation rules and simulation.
