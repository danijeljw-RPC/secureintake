# Branding and Custom Domains

Source: notes v2.3. Resolved questions: [QUESTIONS.md](../QUESTIONS.md) (Q-BRD-*). Planning decisions applied below take precedence over the source notes.

> **Planning decisions applied (2026-11-03)** — these override the text below where they differ. See [QUESTIONS.md](../QUESTIONS.md).
> - Public routes are only `/u/{grantId}` and `/s/{reference}`. **No organisation slugs and no landing page** (Q-LNK-1, Q-BRD-2).
> - v1.0 branding: display name, logo and primary colour. Custom domains and custom sender identity are **v1.1**.
> - Domain tombstone is 90 days. Verification is re-checked daily and suspended after 7 days of failure (Q-BRD-1).

## Decision

Organisations can customise their public upload experience and optionally use **verified custom domains**. **DNS, certificate provisioning and TLS termination are the deployment operator's responsibility.**

Branding is a tenant-scoped **presentation layer**, separate from the encryption protocol, upload workflow and security controls.

> Custom domains change where the portal is presented, not what the portal is authorised to do.

## 1. Branding capabilities

| Setting | Configurable | Constraints |
| --- | --- | --- |
| Display name | Yes | Length and content validation |
| Logo | Yes | Validated image formats |
| Primary / secondary colour | Yes | Contrast requirements |
| Light/dark appearance | Yes | Approved theme tokens |
| Welcome heading | Yes | Plain text |
| Introductory / footer text | Yes | Sanitised, limited formatting |
| Contact information | Yes | Validated |
| Language | Yes | Supported application locales |
| Custom domain | Optional | Verification and operator activation |
| Custom JavaScript | **No** | Prohibited |
| Arbitrary HTML/CSS | **No** | Prohibited |

Branding must never hide required privacy notices, security warnings, upload restrictions or submission confirmations. **Security-critical wording comes from the application**, not organisation-authored content.

Inheritance: `Deployment branding → Organisation branding → Dropbox overrides`. Dropbox overrides are limited to title, description, optional logo and approved theme settings — no per-dropbox CSS/JS.

## 2. Public upload portal

A small, purpose-built application (not the full admin frontend) with explicit stages: access challenge (if required) → uploader identity verification (if required) → file selection → encryption → upload progress → receipt. See [07](07-upload-portal-distribution.md).

## 3. Custom domain model

```text
https://intake.example.com/u/grt_01JD…#siu_…       # platform domain (fallback)
https://documents.example.org/u/grt_01JD…#siu_…    # organisation custom domain
https://documents.example.org/s/SI-8K4P2-M7QXZ#sir_…   # status and receipt (return access)
# Planning decision Q-LNK-1 / Q-BRD-2: the same two routes on every host.
# No /d/{dropboxId}, /upload/{id} or /u/{org-slug} routes, and no organisation landing page.
```

- The platform domain stays available as an admin fallback unless the operator disables it.
- A custom domain belongs to exactly one organisation; multiple per organisation are allowed but only one is canonical at a time.
- Hostnames are normalised with a consistent IDNA implementation, stored canonically, unique on the normalised form. **No wildcard hostnames** initially.

```text
organisation_domains(id, organisation_id, hostname, verification_status, activation_status,
  verification_token_hash, verified_at, verification_expires_at, tls_status, canonical, created_at, updated_at)
```

## 4. Verification (DNS TXT)

```text
_secure-intake.documents.example.org TXT "secure-intake-verification=<random-token>"
```

A TXT challenge is required — an existing CNAME isn't proof of ownership.

1. Admin requests hostname. 2. Platform generates a time-limited random token. 3. Domain owner publishes TXT. 4. Platform validates. 5. Operator configures DNS routing, reverse proxy and TLS externally. 6. Platform confirms routing and TLS readiness, then activates.

Verification expires and is renewed after material ownership changes or a configured interval. Deleting and re-adding a domain never reuses an old token.

## 5. DNS and TLS (operator)

The app gives instructions (e.g. `documents.example.org CNAME intake.example.com`, target is deployment-specific) but doesn't manage DNS providers or CAs.

Production custom domains require: valid chain; hostname-matching certificate; TLS 1.2+ (1.3 preferred); secure reverse-proxy config; correct forwarded-host/proto handling; no insecure HTTP fallback for authenticated operations. The app **verifies the externally visible HTTPS endpoint** before activation (DNS alone doesn't prove secure serving).

## 6. Domain states

```text
requested → pending_verification → verified → pending_tls → ready → active
also: suspended · verification_expired · tls_invalid · removed
```

Invalid TLS is never treated as healthy — alert operator and organisation admin. Temporary TLS failure on an active domain is an incident, never a reason to reassign the hostname.

## 7. Host-based tenant routing

Resolved from the authoritative registry. Never trust arbitrary `Host`/`X-Forwarded-Host`; the reverse proxy has explicit trusted proxy addresses and allowed forwarded headers.

```text
HTTPS request → trusted ingress validates hostname → normalise → lookup active assignment
→ resolve organisation → resolve dropbox/portal route → apply tenant authorisation and policy
```

```csharp
public interface IOrganisationDomainResolver
{
    ValueTask<OrganisationDomain?> ResolveAsync(string hostname, CancellationToken ct = default);
}
public sealed record OrganisationDomain(Guid OrganisationId, string Hostname, bool IsCanonical, long AssignmentVersion);
```

- Resolution happens before tenant-scoped data access; the result is checked against the requested dropbox's organisation (a valid domain never grants access to another organisation's dropbox by ID).
- Unknown/inactive hostnames get a generic error and **never fall back to another organisation's portal**.

## 8. URL design

```text
GET /                      # generic page, no organisation listing
GET /u/{grantId}           # upload (secret in fragment)
GET /s/{reference}         # status and receipt (return-access secret in fragment)
GET /status                # deployment availability
```

- No internal DB IDs in URLs. A public submission reference **does not grant access** — status/return access uses the capability-token and identity-verification model ([12](12-upload-links-and-invitations.md), [14](14-uploader-identity.md)). One-way dropboxes expose no return/status route.
- One canonical public origin per organisation, used for generated links.
- No automatic cross-domain redirects of secret-bearing URLs; never append capability tokens to redirects. If a custom domain changes, old links stay valid under a controlled compatibility policy or are reissued.

## 9. Domain takeover prevention

Ownership verification before activation; unique assignment; reverification on dispute or material change; safe removal workflow; operator warnings about dangling DNS; **assignment tombstones** for a defined period; no automatic reassignment of a previously used hostname. Reassignment requires fresh verification and possibly platform approval. The app can't remove operator DNS records, so removal instructs the operator to do it.

## 10. Browser security

- Host-only session cookies; `Secure`, `HttpOnly`, appropriate `SameSite`; no shared auth cookies across unrelated custom domains.
- Explicit CORS allowlists; CSRF protection on cookie-authenticated operations; strict CSP; `Referrer-Policy: no-referrer`; no third-party scripts on encryption pages; controlled asset loading; strict origin validation for browser API calls.
- Short-lived, origin-bound portal sessions where feasible.
- **Admin UI stays on the platform's trusted admin origin**; custom upload domains don't host administration by default.

**Encryption boundary:** a compromised custom-domain origin or injected JS could compromise client-side encryption — hence no executable code or arbitrary external assets in branding. High-assurance use cases should prefer an independently distributed encryption client ([08](08-reviewer-clients.md)).

## 11. Branding asset security

PNG, JPEG, WebP only (**no SVG** initially). Size-limited; validated by content (not extension); re-encoded where practical; separate asset namespace; fixed content types; protected from HTML execution and sniffing. The platform may inspect branding assets (they're public presentation), which doesn't weaken E2EE for submissions.

## 12. Email identity

Independent from portal domain verification — owning `documents.example.org` doesn't grant sending from it. Requires sender-domain verification, SPF/DKIM, DMARC alignment where applicable, approved `Reply-To`, safe template variables, no arbitrary impersonation ([05](05-notifications.md)).

## 13. Admin UX

**Domains & Branding** section: primary portal domain and status (verified · TLS ready · active), fallback platform URL, DNS verification record, TLS readiness, canonical-domain controls, theme/logo. Activation requires explicit confirmation.

## 14. Data model

```text
organisation_branding               organisation_domain_assignments
organisation_branding_versions      organisation_domain_health_checks
organisation_branding_assets        organisation_email_identities
organisation_domains                organisation_email_identity_verifications
organisation_domain_verifications
```

Domain assignment and branding changes are audited; reassignment is transactional and concurrency-safe.

## 15. API

```text
GET    /api/organisations/{id}/branding
PUT    /api/organisations/{id}/branding
POST   /api/organisations/{id}/branding/assets
GET    /api/organisations/{id}/branding/preview
GET    /api/organisations/{id}/domains
POST   /api/organisations/{id}/domains
POST   /api/organisations/{id}/domains/{domainId}/verify
POST   /api/organisations/{id}/domains/{domainId}/activate
POST   /api/organisations/{id}/domains/{domainId}/deactivate
DELETE /api/organisations/{id}/domains/{domainId}
GET    /api/organisations/{id}/email-identities
POST   /api/organisations/{id}/email-identities
POST   /api/organisations/{id}/email-identities/{identityId}/verify
```

Activation and reassignment need appropriate permissions, recent authentication and possibly platform approval.

## 16. Implementation order

1. Branding model and theme tokens.
2. Secure branding asset pipeline.
3. Portal branding inheritance.
4. Domain registry and hostname normalisation.
5. DNS TXT verification.
6. Operator-managed TLS readiness checks.
7. Host-based tenant routing.
8. Canonical URL generation.
9. Removal and takeover safeguards.
10. Custom email sender verification.
