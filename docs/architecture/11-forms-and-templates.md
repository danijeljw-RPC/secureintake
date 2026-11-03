# Custom Forms, Field-Level Confidentiality and Templates

Sources: notes v2.9 (custom forms) and v3.0 (reusable templates). Resolved questions: [QUESTIONS.md](../QUESTIONS.md) (Q-FRM-*). Planning decisions applied below take precedence over the source notes.

> **Planning decisions applied (2026-11-03)** — these override the text below where they differ. See [QUESTIONS.md](../QUESTIONS.md).
> - Templates are the only form model (Q-FRM-1).
> - Encrypted form payload padding is on by default: 1, 4, 16, 64 and 256 KiB buckets, then 256 KiB multiples (Q-FRM-2).

## Decisions

1. Custom submission forms may mix **server-readable** and **end-to-end encrypted** fields, enforced throughout the lifecycle (API responses, storage, search, workflow automation, notifications, audit, exports).
2. Organisations create **reusable, versioned form templates** assigned to many dropboxes. Each dropbox **pins** a published template version and may apply **controlled presentation overrides**, compiled into an immutable **form snapshot**.

> A field classified as end-to-end encrypted must never require the Secure Intake server to access its plaintext.
> Reusability must not create implicit schema changes: templates are immutable, dropboxes pin explicitly, and every submission retains its exact schema.

---

## Part A — Forms and confidentiality

### A1. Architecture

```text
Published form definition (types · validation · confidentiality · visibility · schema version)
  → Uploader completes form (client validates; separates confidentiality classes)
       ├── Server-readable: validated values · controlled indexing · workflow rules
       └── E2EE: ciphertext · recipient envelopes · client-side validation
  → Submission committed (immutable schema version · authenticated metadata · audit · retention)
```

The renderer is the same frontend in integrated and static deployments ([07](07-upload-portal-distribution.md)). No organisation JavaScript.

### A2. Confidentiality classes

| Class | Server sees value | Searchable | Workflow conditions | Reviewer decryption |
| --- | --- | --- | --- | --- |
| `server_readable` | Yes | If explicitly indexed | Yes, if allowlisted | No |
| `end_to_end_encrypted` | No | No plaintext indexing | No content-based conditions | Required |

Labels/help text/configuration are **schema metadata**, not a value class. **Server-readable ≠ public**: tenant-scoped, RBAC-protected, retained and audited — but the operator-controlled server can technically read them, and organisations must be told so.

### A3. Field types

| Type | Server-readable | E2EE |
| --- | --- | --- |
| Short text, long text, number, date, boolean, single/multi-select, email, telephone, reference number | Yes | Yes |
| File attachment | No plaintext | Yes — existing encrypted-file pipeline |
| Static text / heading | n/a | n/a |

No arbitrary HTML, scripts, custom plugins or executable validation in v1.

### A4. Schema

Platform-specific schema (JSON Schema used internally, not exposed raw):

```json
{
  "formKey": "customer_documents", "version": 3, "title": "Customer Document Submission",
  "fields": [
    { "key": "reference_number", "type": "text", "label": "Reference number", "required": true,
      "confidentiality": "server_readable", "validation": { "minLength": 3, "maxLength": 40 },
      "indexing": { "enabled": true, "mode": "exact" } },
    { "key": "description", "type": "textarea", "label": "Description", "required": false,
      "confidentiality": "end_to_end_encrypted", "validation": { "maxLength": 5000 } },
    { "key": "document_category", "type": "select", "label": "Document category", "required": true,
      "confidentiality": "server_readable", "options": ["identity", "financial", "other"],
      "indexing": { "enabled": true, "mode": "exact" } }
  ]
}
```

Field keys are stable identifiers (not derived from labels). Published schemas are immutable; historical submissions keep their version.

### A5. Validation

**Client** (only place E2EE plaintext is validated): required; length; numeric bounds; date ranges; restricted safe regex subset; allowed options; supported cross-field conditions.

**Server:** full validation of server-readable fields. For E2EE fields only the encrypted representation: expected field IDs; required encrypted fields present; suite; recipient-envelope coverage; ciphertext size limits; authenticated manifest structure; schema version; submission ownership. The server **can't verify** an encrypted field is a valid email/date — inherent to E2EE.

### A6. Conditional fields

```json
{ "field": "financial_details",
  "visibleWhen": { "field": "document_category", "operator": "equals", "value": "financial" } }
```

Browser conditions may reference either class; the server can only evaluate server-readable conditions. Security-sensitive server-enforced requirements must depend only on server-evaluable conditions; the builder flags limitations before publishing.

### A7. Encrypted form payload

Same approved suite and recipient infrastructure as files, but **one encrypted payload** for all E2EE fields (not per-field encryption):

```text
Encrypted Form Payload
├── Format version             ├── Field-value map
├── Organisation ID            ├── Authenticated metadata
├── Submission ID              └── Recipient envelopes
├── Form definition ID + version
```

Client canonically serialises E2EE values, encrypts locally, wraps the DEK for approved recipients. Fewer envelopes, less overhead and fragmentation, simpler interop, and the whole confidential response is authenticated. Values stay bounded; chunking for huge text could come later.

**Binding** — authenticate: `organisation_id, dropbox_id, submission_id, form_definition_id, form_version, encrypted_payload_version, recipient_policy_version` (prevents transplanting payloads or misinterpreting schemas). Server-readable values go into a canonical submission commitment so reviewer clients detect changes — via an authenticated manifest or **keyed** commitment, since plain hashes of low-entropy values leak by guessing.

### A8. Storage

```text
form_definitions            submission_form_responses
form_definition_versions    submission_form_readable_values
form_fields                 submission_form_encrypted_payloads
form_field_options          submission_form_revisions
```

```json
{ "submissionId": "sub_123", "formVersion": 3,
  "readableValues": { "reference_number": "REF-2026-001", "document_category": "financial" },
  "encryptedPayload": { "payloadId": "ep_456", "protocolVersion": 1, "ciphertextLength": 2048, "recipientEnvelopeCount": 3 } }
```

The API never substitutes decrypted E2EE values into readable responses.

### A9. Indexing and search

| Index | Fields |
| --- | --- |
| Exact | Reference numbers, category codes |
| Range | Dates, numbers |
| Prefix | Selected text identifiers |
| Full-text | Explicitly approved server-readable text |
| None | **Default**, especially for sensitive fields |

Never auto-index every field. PostgreSQL: typed projections for searchable fields (not ad-hoc JSONB queries); SQLite: equivalent indexes. The schema compiler produces an **inspectable index plan**; indexes created by controlled migrations/jobs, never user SQL.

```text
POST /api/organisations/{id}/submissions/search
```

```json
{ "filters": [ { "field": "reference_number", "operator": "equals", "value": "REF-2026-001" },
               { "field": "document_category", "operator": "equals", "value": "financial" } ],
  "pageSize": 50 }
```

Filters validated against schema and indexing permissions. Searching an E2EE field → clear unsupported-operation error. **No deterministic encryption or unreviewed blind indexes.**

### A10. Workflow integration

Approved server-readable fields can drive v2.8 rules ([10](10-review-workflows.md)). E2EE values are never available to workflow conditions, notification templates, webhook payloads or assignment rules. Encourage the pattern of a separate server-readable category (`document_category`) beside an E2EE description (`confidential_description`).

### A11. Reviewer experience

Server-readable fields visible immediately (subject to authorisation); E2EE fields stay locked until the reviewer explicitly decrypts locally. Desktop/CLI expose the same separation.

### A12. Retention

One retention unit by default: readable values, encrypted payloads, attached files, recipient envelopes, revisions. Audit and tombstones may differ. **Per-field retention deferred** (would need separate encryption units for selective deletion — documented limitation).

### A13. Privacy and minimisation

The builder asks whether the server genuinely needs each value. Suggested (not fixed): internal reference, document category, processing priority → server-readable; personal circumstances, financial account info, medical info, supporting documents → E2EE.

**Metadata leakage** — even with E2EE the server sees field presence, ciphertext size, timing, organisation and dropbox, recipient key IDs, file count, workflow state and server-readable values. Never claim E2EE hides all metadata. Optional payload padding reduces size leakage at a storage/bandwidth cost.

### A14. Schema changes and revisions

New versions may add/remove fields, change labels, validation, options or confidentiality. Historical submissions keep their schema and encryption interpretation. E2EE → server-readable applies only to new responses; **historical values are never decrypted to backfill**.

Revisions follow the append-only model as **immutable snapshots** in v1:

```text
Submission SI-8K4P2M
├── Revision 1: form v3 · readable values · encrypted payload · files A, B
└── Revision 2: form v3 · updated readable values · new encrypted payload · file C
```

---

## Part B — Reusable templates

### B1. Architecture

```text
Organisation form template (reusable fields · validation · confidentiality)
  → Published immutable version (template ID · version · content digest)
       ├── Finance Dropbox — pinned to v3 + custom heading/help text
       └── Support Dropbox — pinned to v2 + custom heading/branding
  → Compiled dropbox form snapshots (stable schema · fixed confidentiality · validated overrides)
```

Publishing a new template version never silently changes existing dropboxes. No dynamic inheritance resolution at upload time.

### B2. Scope

| Scope | Supported |
| --- | --- |
| Organisation-owned templates | Yes |
| Reuse across the organisation's dropboxes | Yes |
| Cross-organisation sharing | No (initially) |
| Built-in example templates | Yes, **copied** into the organisation |
| Template export/import | Later, with validation |
| Nested template inheritance | **No** (copy/fork instead) |

### B3. Lifecycle

`Draft → Validated → Published → Deprecated → Archived`. Multiple published versions coexist. Deprecation can block new assignments but doesn't break history or pinned dropboxes. Archiving never deletes versions referenced by submissions, holds or audit.

### B4. Template schema and field identity

```json
{ "templateId": "tpl_customer_documents", "version": 3, "name": "Customer Documents",
  "fields": [
    { "id": "fld_reference", "key": "reference_number", "type": "text", "required": true,
      "confidentiality": "server_readable", "validation": { "maxLength": 40 } },
    { "id": "fld_description", "key": "description", "type": "textarea", "required": false,
      "confidentiality": "end_to_end_encrypted", "validation": { "maxLength": 5000 } } ] }
```

Field IDs stay stable across compatible versions; keys are never reused for unrelated meanings; type or confidentiality changes may require a new field identity.

### B5. Reusable field definitions

Organisation field catalogue (customer reference, case category, submission date, contact email, confidential description, internal processing reference):

```json
{ "fieldDefinitionId": "field_customer_reference", "key": "customer_reference", "type": "text",
  "confidentiality": "server_readable", "validation": { "maxLength": 40 } }
```

Immutable versions (`v1 max 20 → v2 max 40 → v3 updated label`). Templates pin a specific field version — never "latest". Changing a shared field (e.g. to E2EE) is adopted deliberately, protecting workflow rules and indexes.

### B6. Controlled overrides

| Property | Override |
| --- | --- |
| Heading, intro text, help text, placeholder | Yes |
| Field display order | Yes, within constraints |
| Optional field visibility | Yes, if template permits |
| Branding | Yes |
| Field type, confidentiality, validation, indexing, workflow metadata classification | **No** |
| Required status | No (initially) |

Overrides change presentation, never data meaning or security. Different validation/confidentiality → new template version or fork.

### B7. Dropbox assignment and compiled snapshots

```text
dropbox_form_assignments(id, organisation_id, dropbox_id, template_id, template_version_id,
  override_version_id, compiled_snapshot_id, activated_at, deactivated_at)
```

Must reference a published version of the **same organisation**. Pending assignments allow future cutover. A dropbox may have no custom form (standard upload experience).

Compiler: load immutable version → resolve pinned field definitions → apply permitted overrides → validate → compute **canonical** digest (plain JSON serialisation is insufficient) → store immutable snapshot → activate for new sessions.

```json
{ "snapshotId": "snap_123", "templateVersionId": "tplv_456", "overrideVersionId": "ovr_789",
  "schemaDigest": "sha256:...",
  "fields": [ { "id": "fld_reference", "key": "reference_number", "label": "Your customer reference",
                "type": "text", "required": true, "confidentiality": "server_readable" } ] }
```

### B8. Publishing (separate from assignment)

1. Edit draft. 2. Validate (correctness, confidentiality, compatibility). 3. Diff against previous version. 4. Preview with the real portal component. 5. Publish (immutable version + digest). 6. Explicitly assign to selected dropboxes.

### B9. Compatibility report

| Change | Compatibility | Action |
| --- | --- | --- |
| Label / help text | Compatible | Publish |
| Add optional field | Usually compatible | Publish |
| Add required field | Potentially breaking | New version; review integrations |
| Remove field | Breaking for consumers | Migration assessment |
| Change type | Breaking | Prefer new field identity |
| Change confidentiality | Security-significant | Explicit review |
| Change options | Potentially breaking | Review rules |
| Change indexing | Operational | Index migration plan |
| Change validation | Potentially breaking | Review client/API compatibility |

Reports both rendering compatibility and API/workflow compatibility.

### B10. Dropbox upgrades

Never automatic: preview differences → validate workflow dependencies → compile snapshot → schedule or activate. In-flight upload sessions stay pinned to their snapshot until expiry (subject to security revocation/compatibility policy).

### B11. Migration types

- **Template migration** — new version; no submission changes.
- **Dropbox assignment migration** — new snapshot for new sessions; no history changes.
- **Historical submission migration** — never automatic; server-readable may be migrated under control; E2EE requires an authorised client. Automatic historical E2EE migration is **deferred**.

Reviewers always see historical submissions rendered with their original snapshot, which is retained as long as referenced data and audit obligations exist.

### B12. Dependency graph

```text
Form field ├── Workflow condition ├── Assignment rule ├── Search index
           ├── Notification template ├── Webhook projection └── External API contract
```

The compiler rejects assignments that invalidate mandatory workflow rules; optional dependencies get explicit warnings and documented fallbacks. E2EE fields never become available to server-side conditions through templates.

### B13. Validator

```csharp
public interface IFormSchemaValidator { FormValidationResult Validate(FormDefinition definition); }
public sealed record FormValidationResult(bool IsValid, IReadOnlyList<FormValidationIssue> Issues);
public sealed record FormValidationIssue(string Code, string Path, string Message, ValidationSeverity Severity);
public enum ValidationSeverity { Information, Warning, Error }
```

Checks: unique keys/IDs; supported types; valid confidentiality; bounded constraints; safe conditions; no circular dependencies; valid field references; override rules; indexing permissions; workflow compatibility; max complexity. The TypeScript renderer uses a **generated** representation of the same contract.

### B14. Complexity limits (illustrative, configurable within deployment limits)

| Limit | Default |
| --- | --- |
| Fields per form | 100 |
| Conditional rules | 100 |
| Max conditional depth | 5 |
| Options per field | 250 |
| Label length | 200 chars |
| Help-text length | 2,000 chars |
| Published versions retained | Configurable |
| Active templates | Organisation quota |

### B15. Admin UI

Desktop-first **form builder** with prominent confidentiality badges (confirmation required to move a field from E2EE to server-readable in a new version). **Form Templates** area shows field counts by class, assigned dropboxes and their pinned versions ("Upgrade available"). Bulk upgrades produce a per-dropbox compatibility report first.

---

## Data model

```text
form_definitions                          organisation_form_templates
form_definition_versions                  organisation_form_template_versions
form_fields                               organisation_field_definitions
form_field_options                        organisation_field_definition_versions
submission_form_responses                 form_template_field_references
submission_form_readable_values           form_template_validation_reports
submission_form_encrypted_payloads        dropbox_form_assignments
submission_form_revisions                 dropbox_form_override_versions
                                          dropbox_compiled_form_snapshots
                                          form_template_publication_events
                                          form_template_upgrade_operations
                                          form_template_dependency_references
```

Published versions and snapshots are immutable; digests and audit detect unexpected changes.

## API

```text
GET  /api/organisations/{id}/form-templates
POST /api/organisations/{id}/form-templates
GET  /api/organisations/{id}/form-templates/{templateId}
POST /api/organisations/{id}/form-templates/{templateId}/validate
POST /api/organisations/{id}/form-templates/{templateId}/publish
GET  /api/organisations/{id}/field-definitions
POST /api/organisations/{id}/field-definitions
GET  /api/organisations/{id}/dropboxes/{dropboxId}/form-assignment
PUT  /api/organisations/{id}/dropboxes/{dropboxId}/form-assignment
POST /api/organisations/{id}/dropboxes/{dropboxId}/form-preview
POST /api/organisations/{id}/dropboxes/{dropboxId}/form-upgrade-check
GET  /api/organisations/{id}/submissions/{submissionId}/form
GET  /api/organisations/{id}/submissions/{submissionId}/form/encrypted-payload
POST /api/organisations/{id}/submissions/search

GET  /api/public/dropboxes/{dropboxId}/form                      # published, public-safe snapshot only
POST /api/public/upload-sessions/{sessionId}/form-response       # requires valid session capability
```

v2.9's per-dropbox `/forms` endpoints are **dropped**. Every dropbox form is a template assignment, and a dropbox-only form is a private template created automatically (Q-FRM-1).

## Implementation order

1. Form/template schema and validator.
2. Organisation template registry and immutable versions.
3. Reusable field definitions.
4. Dropbox version pinning, controlled overrides, compiled snapshots.
5. Public form renderer.
6. Server-readable validation and storage.
7. Encrypted form payload protocol and envelope integration.
8. Reviewer decryption support.
9. Controlled indexing and search.
10. Workflow field allowlisting and dependency analysis.
11. Upgrade preview and bulk assignment.
12. Retention, revisions and privacy validation.
