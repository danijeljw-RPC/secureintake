# Observability and Operations Dashboard

Source: notes v4.1 (with health/metrics notes from v2.1). Resolved questions: [QUESTIONS.md](../QUESTIONS.md) (Q-OBS-*). Planning decisions applied below take precedence over the source notes.

> **Planning decisions applied (2026-11-03)** — these override the text below where they differ. See [QUESTIONS.md](../QUESTIONS.md).
> - Tenant IDs in telemetry are opt-in and HMAC-pseudonymised (Q-OBS-1). Alert history 13 months, security findings 24 months (Q-OBS-2).

## Decision

Secure Intake includes a **built-in operations dashboard** alongside **OpenTelemetry-based observability**. The dashboard works in standalone deployments without Grafana, Prometheus, Elasticsearch or similar; operators can additionally export telemetry to their own stack.

Three kinds of operational information, each with different reliability, privacy and retention:

| Kind | Examples | Authority |
| --- | --- | --- |
| Telemetry | Logs, metrics, traces | Best-effort, sampled |
| Operational state | Durable jobs, storage health, backlogs, service status, security findings | Durable DB state |
| Audit evidence | Security-sensitive actions, config changes, submission lifecycle | Independent audit subsystem (hash chain, signed checkpoints) |

The dashboard is **not** a second audit system. Telemetry never exposes submission content (encrypted or not) or private keys.

## 1. Architecture

```text
API nodes (requests · auth · uploads · errors)   Workers (jobs · receipts · exports · cleanup)
                       ↓
Instrumentation + privacy enforcement (OTel API · typed attributes · redaction · cardinality limits)
        ↓                              ↓                               ↓
Built-in operations             OTel exporters (OTLP)          Independent audit subsystem
(DB state · aggregates ·        metrics · logs · traces        (transactional · hash chain ·
 alerts · diagnostics)                                          signed checkpoints)
```

The dashboard reads durable operational state and bounded aggregates — never relies on collecting every trace or log.

| Capability | Standalone | Distributed |
| --- | --- | --- |
| Built-in dashboard | Yes | Yes |
| Structured JSON logs | Yes | Yes |
| OTel metrics | Yes | Yes |
| Distributed traces | Optional | Yes |
| External OTLP collector | Optional | Optional |
| Durable job monitoring | SQLite | PostgreSQL |
| Alert evaluation | Combined host | Leased worker |
| Operational history | Bounded local storage | Bounded shared storage |

The dashboard works when exporters are down; API and workers run when the dashboard is disabled.

## 2. Instrumentation

Standard .NET `ActivitySource`, `System.Diagnostics.Metrics` and OpenTelemetry. Namespaces, each with documented metrics, activity names and permitted attributes:

```text
SecureIntake.Api            SecureIntake.Submissions    SecureIntake.Notifications
SecureIntake.Authentication SecureIntake.Receipts       SecureIntake.Workers
SecureIntake.Uploads        SecureIntake.Evidence       SecureIntake.Security
SecureIntake.Storage        SecureIntake.Workflows      SecureIntake.Recovery
SecureIntake.Cryptography
```

```csharp
public static class IntakeTelemetry
{
    public const string Name = "SecureIntake.Submissions";
    public static readonly ActivitySource Activities = new(Name);
    private static readonly Meter Meter = new(Name);

    public static readonly Counter<long> SubmissionsReceived =
        Meter.CreateCounter<long>("secureintake.submissions.received", unit: "{submission}");
    public static readonly Histogram<double> FinalisationDuration =
        Meter.CreateHistogram<double>("secureintake.submissions.finalisation.duration", unit: "s");
    public static readonly Counter<long> FinalisationFailures =
        Meter.CreateCounter<long>("secureintake.submissions.finalisation.failures", unit: "{failure}");
}
```

Failure attributes use bounded, predefined reason codes — never raw exception messages.

## 3. Telemetry privacy policy

Strict attribute allowlist, applied to built-in telemetry and external exporters alike:

| Data | Metrics | Logs | Traces |
| --- | --- | --- | --- |
| Operation name, status/error category, durations, byte counts, service/node ID | Yes | Yes | Yes |
| Organisation ID | No (default) | Restricted | Restricted |
| Submission ID | No | Restricted | Restricted |
| Uploader email/IP | No | Security-only when necessary | No |
| Original filenames, form answers | No | No | No |
| Ciphertext or plaintext | No | No | No |
| Access tokens, private keys | No | No | No |

- No automatic request/response body or auth-header capture. Query strings stripped or sanitised.
- **Cardinality:** metric labels restricted to bounded sets (`operation`, `result`, `provider_type`, `service_role`, `error_category`, `deployment_profile`). Never submission/user IDs, emails, object keys or URLs.

## 4. Dashboard

Available in the admin web UI with **platform** and **organisation** scopes. Organisation admins never see platform topology.

| Section | Contents |
| --- | --- |
| Overview | Health, traffic, failures, latency, alerts |
| Jobs & Queues | Backlog, retries, leases, dead-letter |
| Storage | Connectivity, quotas, integrity, orphaned objects |
| Security | Auth failures, suspicious activity, key issues |
| Integrations | SMTP, webhooks, OIDC, secret providers |
| Diagnostics | Node status, versions, config drift, recovery |

- Every measurement states its scope ("100% of objects checked" ≠ "every object is intact").
- Audit UI is separate, linked from relevant events.
- **Mobile:** read-only overview and basic alert acknowledgement only. Job replay, policy changes, key operations, recovery activation and other high-impact actions are **desktop-only**.

## 5. Health checks

```text
GET /health/live     # process running; not failed by e.g. SMTP outage
GET /health/ready    # API: authoritative DB reachable, schema/config compatible; worker: can safely claim jobs
GET /health/status   # authorised detail
```

```json
{ "status": "degraded",
  "components": { "database": "healthy", "storage": "healthy", "receiptSigning": "healthy", "notifications": "degraded" },
  "configurationGeneration": 42, "applicationVersion": "4.1.0" }
```

Bounded timeouts; no expensive full scans in probes.

## 6. Jobs and queues

The durable worker system is authoritative. Expose: pending by type; oldest pending age; active/expired leases; retry counts; dead-letter; processing duration; success/failure rates; worker compatibility issues; jobs blocked by config or unavailable keys. Distinguish transient failures, terminal failures and jobs awaiting operator action. A failed job ≠ a lost submission.

### Job administration

| Action | Behaviour |
| --- | --- |
| Inspect | Status, attempts, safe error details, dependencies |
| Retry | Requeue a retryable failed job |
| Pause / Resume type | Stop/restart claiming a job category |
| Cancel | Request cancellation where supported |
| Release lease | Recover an abandoned lease after fencing checks |
| Dead-letter | Quarantine a permanently failing job |
| Reconcile | Compare job state with the underlying operation |

Every handler declares a replay policy that the dashboard cannot bypass (e.g. webhook replay may duplicate notifications; receipt signing must never create a conflicting receipt):

```csharp
public enum JobReplayPolicy { Idempotent, ReconcileBeforeReplay, ApprovalRequired, NotReplayable }
```

## 7. Security alerts

Default catalogue (severity driven by verified signals, not thresholds alone; an alert is not proof of malice):

| Alert | Severity |
| --- | --- |
| Receipt-signing key compromised | Critical |
| Audit-chain checkpoint mismatch | Critical |
| Cross-tenant isolation violation | Critical |
| Ciphertext digest mismatch | Critical |
| Recovery/deployment identity mismatch | Critical |
| Unauthorised administrative access attempt | High |
| Repeated privileged auth failures | High |
| Unexpected signing-key rotation | High |
| Suspicious bulk evidence exports | High |
| Secret-provider auth failure | High |
| Repeated uploader verification failures | Medium |
| Abnormal upload activity | Medium |
| Configuration drift | Medium |
| Elevated API error rate | Warning |

Lifecycle: `Detected → Open → Acknowledged → Investigating → Resolved → Closed`, plus `suppressed` and `false_positive` with explicit reasons. Acknowledging ≠ resolving.

Channels: dashboard, email, signed outgoing webhooks, OTel export — routed separately from uploader notifications ([05](05-notifications.md)). No mandatory PagerDuty/Slack.

### Rule engine

Constrained rules, not scripts:

```json
{ "ruleId": "receipt-signing-backlog", "enabled": true,
  "signal": "receipt.signing.oldest_pending_seconds",
  "condition": { "operator": "greater_than", "value": 300 },
  "evaluationWindowSeconds": 120, "severity": "high", "cooldownSeconds": 900 }
```

Supports threshold, sustained condition, absence of signal and explicit security-event matching. Evaluator has bounded complexity, deduplication, cooldowns, recovery detection, durable state, maintenance suppression windows and notification-loop protection. In distributed mode one worker holds the evaluation lease per rule scope; a missing evaluator heartbeat is itself a warning.

## 8. Performance metrics

| Area | Metrics |
| --- | --- |
| API | Requests/sec, error rate, p50/p95/p99 latency |
| Uploads | Active sessions, bytes/sec, failures, resumptions |
| Finalisation | Duration, verification failures, pending receipts |
| Storage | Operation latency, failures, quota usage |
| Database | Query latency, pool saturation, transaction failures |
| Workers | Throughput, backlog age, retries, lease expiry |
| Signing | Latency, failures, provider availability |
| Evidence exports | Throughput, package size, verification failures |
| Authentication | Failure categories, challenge latency |
| Notifications | Queue age, delivery attempts, provider acceptance |

Report ciphertext throughput separately from plaintext size (if both are safe). Provider acceptance ≠ email delivered.

## 9. OpenTelemetry setup

```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(serviceName: "secureintake-api", serviceVersion: releaseVersion))
    .WithMetrics(m => m.AddMeter("SecureIntake.Api", "SecureIntake.Submissions", "SecureIntake.Workers")
        .AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddOtlpExporter())
    .WithTracing(t => t.AddSource("SecureIntake.Api", "SecureIntake.Submissions", "SecureIntake.Workers")
        .AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddOtlpExporter());
```

OTel packages must pass the Native AOT matrix; replace unsupported instrumentation with explicit instrumentation.

```json
{ "observability": { "enabled": true,
    "tracing": { "enabled": true, "samplingRatio": 0.05 },
    "metrics": { "enabled": true },
    "otlp": { "enabled": false, "endpoint": "http://otel-collector:4317" } } }
```

A disabled exporter never fails startup; an unavailable one uses bounded buffering and controlled dropping — never exhausting memory or blocking submission processing.

## 10. Built-in history and retention

Bounded aggregation (configurable defaults, not compliance requirements):

| Resolution | Retention |
| --- | --- |
| 1 minute | 24 hours |
| 5 minutes | 7 days |
| 1 hour | 90 days |
| 1 day | 12 months |

Store counts, sums, durations and histograms (averages alone are insufficient). Strict limits on SQLite; partitioning/bounded deletion on PostgreSQL. **No raw traces stored indefinitely in the app DB.**

| Category | Owner | Approach |
| --- | --- | --- |
| Application logs | Operator | External retention |
| Distributed traces | Operator | Short-lived, sampled |
| Built-in metric aggregates | Secure Intake | Bounded |
| Alert history | Secure Intake | Operational retention policy |
| Security findings | Secure Intake | Security retention policy |
| Audit records | Secure Intake | Independent audit retention |

Deleting telemetry never deletes audit evidence; keeping an audit event doesn't require keeping its trace.

## 11. Diagnostic bundles

```bash
secureintake diagnostics collect --output ./secureintake-diagnostics.zip
secureintake diagnostics inspect --bundle ./secureintake-diagnostics.zip
```

May include: versions, config fingerprints, redacted config summary, health results, queue aggregates, storage connectivity, recent safe error categories, migration history, relevant non-sensitive logs. **Excludes** credentials, private keys, tokens, uploaded content, confidential form values and unrestricted DB dumps. Requires an explicit scope and review before external sharing.

## 12. Access control

Uses the platform/organisation RBAC model ([22](22-access-control.md)):

```text
operations.dashboard.view     operations.alerts.view         operations.diagnostics.export
operations.health.view        operations.alerts.acknowledge  operations.security.view
operations.jobs.view          operations.alerts.manage
operations.jobs.manage
```

Organisation admins see only their organisation and must not be able to infer other tenants' activity from global counts, storage metrics or findings — avoid granular cross-tenant usage on shared infrastructure.

## 13. API

```text
GET  /api/platform/operations/overview
GET  /api/platform/operations/metrics
GET  /api/platform/operations/nodes
GET  /api/platform/operations/jobs
GET  /api/platform/operations/jobs/{id}
POST /api/platform/operations/jobs/{id}/retry
GET  /api/platform/operations/alerts
GET  /api/platform/operations/alerts/{id}
POST /api/platform/operations/alerts/{id}/acknowledge
GET  /api/organisations/{id}/operations/overview
GET  /api/organisations/{id}/operations/alerts
```

Bounded date ranges, result limits and scopes. No arbitrary SQL, raw telemetry queries or unrestricted trace search.

## 14. Data model

```text
operational_metric_aggregates   operational_alert_instances    operational_node_heartbeats
operational_health_snapshots    operational_alert_transitions  operational_diagnostic_runs
operational_alert_rules
```

## 15. Audit

```text
operations.alert.acknowledged | resolved | suppressed
operations.job.retry_requested | cancel_requested | lease_released
operations.alert_rule.created | updated | disabled
operations.diagnostics.generated | downloaded
```

Routine metric collection and probes are not audited. Security-sensitive actions use the transactional audit/outbox. Telemetry failures never silently disable mandatory auditing.

## 16. Failure isolation

- OTLP outages never block API requests.
- Full buffers drop telemetry, never submission data.
- Dashboard query failures don't interrupt workers.
- Aggregation jobs run below submission processing priority.
- Security alert state persists independently of email/webhook delivery; findings are recorded durably **before** notification.
- Mandatory audit follows the fail-closed policy for security-sensitive transactions.
- Alert evaluator failure is visible via its own heartbeat.

## 17. Implementation order

1. Typed instrumentation catalogue and privacy policy.
2. OTel metrics and traces.
3. Structured JSON logging and sanitisation.
4. Durable health snapshots and node heartbeats.
5. Bounded metric aggregation.
6. Overview dashboard.
7. Job and queue monitoring.
8. Security alert catalogue and rule evaluator.
9. Controlled job administration.
10. Diagnostic bundles, retention and operational audit.
