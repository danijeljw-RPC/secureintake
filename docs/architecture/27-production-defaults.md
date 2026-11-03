# Production Defaults

Status: **planning decision, 2026-11-03.** Replaces every value the notes marked "illustrative" (QUESTIONS.md §4). They ship in `configuration.schema.json`, with the Production values as defaults and the Hardened values applied by the profile.

The rules:

- An organisation may tighten any value but never loosen it past the deployment value ([23](23-security-policy.md)).
- Quotas are the exception: an operator may raise them ([04](04-resource-governance-and-quotas.md)).
- **Max** means a hard ceiling that configuration can't exceed.

## Onboarding and tenancy

| ID | Setting | Production | Hardened | Max |
| --- | --- | --- | --- | --- |
| Q-ONB-7 | Initial admin / member invitation expiry | 72 h | 24 h | 7 d |
| Q-ONB-7 | Pending provisioning request expiry | 14 d | 7 d | 30 d |
| Q-ONB-4 | Offboarding export window | 30 d | 30 d | 180 d |
| Q-TEN-5 | Tenant migration rollback window | 7 d | 7 d | 30 d |
| Q-TEN-5 | Presigned-capability drain before cutover | max presigned lifetime + 5 min (15 min) | same | n/a |

## Resources and quotas (Q-RES-3, Q-RES-5)

| Scope | Setting | Default |
| --- | --- | --- |
| Deployment | Storage budget | **Required in Production.** No default; startup readiness fails without it. Development: unlimited |
| Deployment | Concurrent upload sessions | 200 |
| Deployment | Worker concurrency | `2 × CPU cores` per worker process, capped at 32 |
| Deployment | **Reserved operational capacity** | 5% of storage budget (min 10 GiB), 2 worker slots for the `essential` class, and a separate admin API rate bucket |
| Organisation | Storage quota | 50 GiB |
| Organisation | Concurrent upload sessions | 20 |
| Organisation | Active dropboxes | 100 |
| Organisation | Webhook deliveries | 10,000 / hour (backpressure, not drop) |
| Dropbox | Max file size | 2 GiB (Max 20 GiB) |
| Dropbox | Max submission size | 5 GiB (Max 50 GiB) |
| Dropbox | Max files per submission | 50 (Max 1,000) |
| Dropbox | Concurrent upload sessions | 10 |
| Dropbox | Completed submissions | 1,000 / day |

### Storage thresholds

- Warning at 80%. Critical at 95%, which alerts only (Q-RES-4).
- At 100%: hard reject of new reservations.
- An organisation policy may restrict new intake at the critical threshold instead.

## API rate limits

| Endpoint class | Limit |
| --- | --- |
| Authenticated principal (human) | 600 req/min, burst 100 |
| API client | 1,200 req/min, burst 200 |
| Grant redemption | 10/min per IP · 100/min per grant |
| OTP / PIN verify | 5 attempts per challenge · 20/hour per IP |
| OTP send | 3 per challenge · 10/hour per address |
| Upload session creation | 20/hour per IP per grant |
| Admin "essential" bucket | Separate from the above, so it's never starved by tenant traffic |

## Upload sessions (Q-UPL-2, Q-LNK-2)

| Setting | Production | Hardened |
| --- | --- | --- |
| Chunk size | 4 MiB plaintext (fixed by protocol `SI-E2EE-1`) | same |
| Initial session lifetime | 24 h | 12 h |
| Absolute maximum lifetime | 7 d | 3 d |
| Inactivity timeout | 24 h | 12 h |
| Presigned upload URL lifetime | 10 min | 5 min |
| Cleanup grace period | 1 h | 1 h |
| Invitation default expiry | 14 d (Max 90 d) | 7 d (Max 30 d) |
| Grace on ordinary grant expiry (in-progress sessions) | 1 h | 15 min |
| Grace on revocation | none (immediate) | none |
| Abuse signals (raw IP, user agent) retention | 30 d | 30 d |
| Abuse signals (aggregated, IP-truncated) retention | 90 d | 90 d |

## Sessions, MFA and approvals (Q-POL-1)

| Setting | Production | Hardened |
| --- | --- | --- |
| Privileged MFA max age | 300 s | 120 s |
| Minimum privileged assurance | Elevated (TOTP allowed) | Phishing-resistant |
| Admin session idle timeout | 30 min | 15 min |
| Admin session absolute lifetime | 12 h | 8 h |
| Reviewer session idle / absolute | 60 min / 12 h | 30 min / 8 h |
| Uploader access session | 60 min | 30 min |

| Sensitive operation | Approvers (Production) | Approvers (Hardened) |
| --- | --- | --- |
| Export one evidence package | 1 | 1 |
| Bulk evidence export | 2 | 2 (plus desktop/CLI only) |
| Change signing-key trust / activate signing key | 2 | 2 (Security Admin **and** Owner slots) |
| Activate E2EE recovery | 2 | 2 plus custodian |
| Reviewer key export (`.sik`) | 1 | 2 |
| Native evidence restoration (import) | 1 | 2 |
| Change deployment security baseline | 2 | 2 |
| Organisation deletion | 2 | 2 |
| Release legal hold | 1 | 2 |
| Policy lockout exception (Q-POL-2) | 2 (platform Owner and Security Admin) | same |

Approval validity is 24 h (Hardened 4 h). Self-approval is always prohibited.

## Keys (Q-KCR-3)

| Setting | Production | Hardened |
| --- | --- | --- |
| Max active reviewer devices | 5 | 3 |
| Recipient key expiry | 2 years | 1 year |
| Expiry warning | 30 d before | 60 d before |
| Recovery recipient for new organisations (Q-KCR-2) | **Required** (an Owner may opt out with a recorded acknowledgement) | Required, no opt-out |
| Receipt / evidence / audit signing-key rotation | 12 months | 6 months |

## Branding and domains (Q-BRD-1)

| Setting | Default |
| --- | --- |
| Custom-domain tombstone after removal | 90 d |
| Domain verification re-check | Every 24 h. Assignment suspended after 7 consecutive days of failure |

## Observability and retention (Q-OBS-2, Q-BAK-2)

| Setting | Production | Hardened |
| --- | --- | --- |
| Alert history | 13 months | 24 months |
| Security findings | 24 months | 36 months |
| Restore drill readiness warning (no drill recorded) | 180 d | 90 d |
| Audit checkpoint interval | 1,000 records / 15 min | 500 records / 5 min |

## Retention (F10)

| Setting | Default |
| --- | --- |
| Organisation default retention | 1 year from finalisation, `onExpiry = review` |
| Deployment minimum retention | 30 d |
| Receipt and tombstone retention after content deletion | 7 years |
