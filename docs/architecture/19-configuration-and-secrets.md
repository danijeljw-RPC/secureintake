# Configuration and Secrets

Sources: notes v3.9 (layered configuration, pluggable secrets), with secret categories and environment guidance from v2.1. Resolved questions: [QUESTIONS.md](../QUESTIONS.md) (Q-CFG-*). Planning decisions applied below take precedence over the source notes.

> **Planning decisions applied (2026-11-03)** — these override the text below where they differ. See [QUESTIONS.md](../QUESTIONS.md).
> - camelCase JSON and `SECUREINTAKE__Section__Key` (Q-CFG-1). **No `Deployment.Mode`**: role comes from `serve --role`, and multi-instance SQLite is refused (Q-CFG-2).
> - Secret providers: env and file in v1.0 → Vault/OpenBao in v1.1 → Azure Key Vault and AWS in v1.2 (Q-CFG-3).

## Decision

**Layered configuration with pluggable secret providers.** Operators can run on local configuration and environment variables, or integrate external secret stores, with no code changes. No mandatory cloud provider, configuration server or secrets service.

Three configuration categories with different ownership, validation and audit rules:

| Category | Contents | Lives in |
| --- | --- | --- |
| Deployment configuration | Infrastructure, endpoints, DB connections, storage profiles, auth, service behaviour | Files / env / CLI |
| Secret references | Credentials and cryptographic material via approved providers | Secret providers |
| Runtime organisation configuration | Tenant policies, dropboxes, workflows, app settings | Tenant database |

## 1. Architecture

```text
Configuration files (base · environment · deployment)   Environment / CLI overrides
                         ↓
Composition + validation (known schema · precedence · deployment profile · cross-field constraints)
                         ↓
Secret reference resolver (environment · file · encrypted local store · external provider)
                         ↓
Validated immutable runtime snapshot → API · Workers · Signing · Storage · Authentication
```

No component reads arbitrary files or environment variables after startup; components consume typed configuration or narrowly scoped secret handles.

## 2. Precedence (highest first)

| # | Source | Purpose |
| --- | --- | --- |
| 1 | Explicit CLI overrides | One-off operational commands |
| 2 | `SECUREINTAKE__` environment variables | Deployment overrides |
| 3 | Deployment-specific config (`deployment.json`) | Host/container settings |
| 4 | Environment-specific config (`appsettings.Production.json`) | Production, development, etc. |
| 5 | Base config (`appsettings.json`) | Shared defaults |
| 6 | Built-in defaults | Safe documented defaults |

- Overrides apply only where a setting is explicitly overridable. **A production security requirement cannot be weakened by a higher-priority source.**
- The environment is selected explicitly; production never silently falls back to development settings.
- Overrides are parsed into typed settings — no scattered `Environment.GetEnvironmentVariable`.

```bash
export SECUREINTAKE__Storage__DefaultProfile="primary-s3"
export SECUREINTAKE__Api__PublicBaseUrl="https://intake.example.com"
```

## 3. Secret references

Secrets are referenced, not embedded. Plaintext credentials in production config files are prohibited unless delivered via a protected secret-file mechanism.

```json
{
  "database": { "provider": "postgresql", "connectionString": "secret://env/SECUREINTAKE_DB_CONNECTION" },
  "storage": {
    "provider": "s3",
    "accessKeyId": "secret://env/SECUREINTAKE_S3_ACCESS_KEY",
    "secretAccessKey": "secret://env/SECUREINTAKE_S3_SECRET_KEY"
  },
  "receiptSigning": { "provider": "external", "keyReference": "key://azure/secure-intake-receipts" }
}
```

- `secret://` resolves confidential bytes/strings.
- `key://` references a cryptographic operation provider that may never expose private key material (KMS signing is used via a signing interface, not by fetching key bytes).

### Secret categories

| Secret | Used for |
| --- | --- |
| Database credentials | Control-plane and tenant DB access |
| Storage credentials | Ciphertext object operations |
| Authentication signing keys | Tokens and session infrastructure |
| Session encryption keys | Protecting session state |
| Webhook signing secrets | Authenticating outgoing events |
| Audit signing keys | Audit checkpoint signatures |
| Receipt signing keys | Submission receipts ([15](15-receipts.md)) |
| Email provider credentials | Verification and invitation delivery |
| External OIDC credentials | IdP integrations |

**Organisation private decryption keys are deliberately excluded — the intake server should not possess them.**

## 4. Providers

| Provider | Use case | Initial release |
| --- | --- | --- |
| Environment | Containers, simple deployments | Yes |
| Protected file | Docker secrets, mounted files, systemd credentials | Yes |
| Local encrypted store | Standalone deployments | Yes |
| Azure Key Vault | Azure-hosted | Optional module |
| AWS Secrets Manager / KMS | AWS-hosted | Optional module |
| HashiCorp Vault | Independent deployments | Optional module |
| PKCS#11 / HSM | Advanced signing/key custody | Later |

The core requires none of the external providers.

```csharp
public interface ISecretProvider
{
    string Scheme { get; }
    ValueTask<SecretValue> ResolveAsync(SecretReference reference, CancellationToken ct = default);
}

public sealed record SecretReference(string Scheme, string Identifier, string? Version);

public sealed class SecretValue : IDisposable
{
    private byte[]? _bytes;
    public SecretValue(byte[] bytes) => _bytes = bytes;
    public ReadOnlyMemory<byte> Value => _bytes ?? throw new ObjectDisposedException(nameof(SecretValue));
    public void Dispose()
    {
        if (_bytes is null) return;
        CryptographicOperations.ZeroMemory(_bytes);
        _bytes = null;
    }
}
```

Zeroisation limits secret lifetime but can't be guaranteed (runtime copies, SDKs, OS memory). Avoid needless string conversion of binary secrets. Secrets are redacted from logs, diagnostics, exceptions and support bundles.

## 5. Native AOT compatibility

Target is **.NET 10 Native AOT**, so no runtime assembly scanning or dynamic loading. Providers are registered explicitly at compile time and included at build time (auditable provider set, trimming-safe):

```csharp
var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.AddSecureIntakeConfiguration();
builder.Services.AddEnvironmentSecretProvider();
builder.Services.AddFileSecretProvider();
#if AZURE_SECRETS
builder.Services.AddAzureKeyVaultSecretProvider();
#endif
#if HASHICORP_VAULT
builder.Services.AddVaultSecretProvider();
#endif
var app = builder.Build();
app.MapSecureIntakeEndpoints();
app.Run();
```

Use JSON source generation (`JsonSerializerContext`) for configuration types. Configuration parsing, validation and provider SDKs must be covered by Native AOT publish tests.

## 6. Typed schema

```csharp
public sealed record SecureIntakeConfiguration
{
    public required DeploymentConfiguration Deployment { get; init; }
    public required ApiConfiguration Api { get; init; }
    public required DatabaseConfiguration Database { get; init; }
    public required StorageConfiguration Storage { get; init; }
    public required SecurityConfiguration Security { get; init; }
}

public sealed record DeploymentConfiguration
{
    public required string DeploymentId { get; init; }
    public required DeploymentProfile Profile { get; init; }   // Development | Production | Hardened
    public required string Environment { get; init; }
}
```

Unknown security-sensitive properties are rejected. Deprecated properties warn explicitly and have defined removal versions.

## 7. Deployment profiles vs environments

**Profiles** are policy baselines:

| Profile | Purpose | Behaviour |
| --- | --- | --- |
| Development | Local testing | Explicitly relaxed, development-only controls |
| Production | Normal deployment | Secure defaults, strict validation |
| Hardened | High assurance | Stronger key custody, audit and access requirements; **fails startup** if required controls are missing |

Production minimum: HTTPS on public origins; no development credentials; no default signing keys; explicit trusted hosts; approved secret providers; no plaintext secrets in config files; strict CORS/CSP; valid DB and storage connectivity; working receipt signing; audit checkpoint signing configured; no insecure auth fallback.

Hardened additions (configurable): external signing-key custody, stronger admin MFA, restricted exports, explicit security approval for high-risk configuration changes.

**Environments** (`development`, `test`, `staging`, `production`) are deployment labels, not security profiles. Staging can (and should, where practical) run the production profile and mirror production behaviour. Development conveniences need explicit opt-in, never implied by the environment name.

## 8. Environment isolation and binding

Deployment identity is explicit and immutable after provisioning:

```json
{ "deployment": { "deploymentId": "dep_prod_01", "environment": "Production", "profile": "Production" } }
```

Production and UAT use separate databases, storage (independent credentials), signing keys, auth client registrations, webhook credentials, email sender config, secret-provider namespaces and audit checkpoint identities. UAT must never send production notifications or read production ciphertext.

The database records its expected deployment ID; a mismatch at startup **blocks startup** unless an authorised restore/migration procedure permits it.

## 9. Validation stages

1. **Structural** — required properties, types, enums, URI syntax, duplicate IDs, unsupported keys.
2. **Security** — TLS, allowed algorithms, secret-reference schemes, signing-key policy, host routing, auth settings, profile constraints.
3. **Operational preflight** — DB connectivity, storage permissions, secret resolution, signing operations, external services.

Static validation is separate from live preflight so config can be checked before infrastructure exists. Output never prints secrets:

```text
[PASS] Configuration schema
[PASS] Deployment identity
[PASS] Production security profile
[PASS] Database configuration
[PASS] Storage profile definitions
[PASS] Secret references
[PASS] Receipt signing configuration
[WARN] SMTP connectivity not tested
[PASS] Audit checkpoint signing
Result: VALID WITH WARNINGS
```

## 10. CLI

```bash
secureintake config validate
secureintake config show --redacted
secureintake config explain Database.ConnectionString
secureintake config diff --against ./previous-config.json
secureintake secrets test
secureintake secrets providers
secureintake preflight
```

`config explain` shows provenance (value or `[REDACTED]`, source file/variable, reference, resolved status, effective profile, whether restart is required) and never reveals resolved secrets.

## 11. Rotation

Rotation needs no rebuild and no manual DB edits.

| Secret | Approach |
| --- | --- |
| Database credentials | Provider-supported rollover, reconnect |
| S3/R2 credentials | Overlapping credentials, then revoke old |
| SMTP credentials | Replace and reconnect |
| Webhook signing secrets | Versioned overlap and controlled retirement |
| Receipt signing keys | Formal key lifecycle; history verifiable ([24](24-key-governance.md)) |
| Audit signing keys | Versioned signing epochs, trust continuity |
| Server-side encryption keys | Explicit key-version and re-encryption workflow |

```text
Prepared → Activated → Overlap → Old credential retired → Completed
```

(Operator view: install new secret → validate access → activate new version → drain old usage → revoke old.) Where a provider only supports immediate replacement, document the availability impact. Each supported rotation procedure is documented explicitly.

## 12. Runtime reload

| Class | Example | Behaviour |
| --- | --- | --- |
| Dynamic | Rate limits, notification defaults | Validated reload |
| Reconnectable | SMTP or storage credentials | Controlled client replacement |
| Restart-required | DB provider, deployment identity, crypto protocol baseline | Restart |

Reloads are atomic via immutable snapshots; each request/job sees one coherent generation. Secret versions are tracked separately where appropriate. Long-running jobs (e.g. exports) never switch storage profile or signing identity midway.

```csharp
public sealed record RuntimeConfigurationSnapshot(long Generation, DateTimeOffset ActivatedAt, SecureIntakeConfiguration Configuration);
```

## 13. Resolution failure

- **Startup-critical** (primary DB credential, required receipt/audit signing, required storage credentials): block startup or keep the role not-ready.
- **Runtime-optional** (optional email provider, disabled integrations, nonessential webhook credentials): disable the feature and raise a health warning.
- Never fall back to insecure defaults.

## 14. Local encrypted secret store (standalone)

```bash
secureintake secrets init --provider local
secureintake secrets set Database.ConnectionString
secureintake secrets list
secureintake secrets rotate Database.ConnectionString
```

Established authenticated encryption, versioned format. The master key comes from **outside** the store (operator file, injected secret, OS key facility) — a key beside the data with identical access offers little protection. Operators back up store data, master-key recovery material, version metadata and provider configuration; losing the master key may make the store unrecoverable.

## 15. Drift detection (multi-node)

Each node records a non-secret fingerprint (canonical, redacted representation; secret versions as opaque IDs):

```text
node_id, deployment_id, configuration_generation, configuration_fingerprint, application_version, activated_at
```

Nodes must agree on: deployment identity, DB routing, crypto protocol policy, trusted hosts, receipt-signing policy, audit checkpoint config, supported API/storage protocol versions. Noncritical settings (e.g. listening ports) may differ by role. Incompatible nodes **fail readiness**.

There's no central config control plane; distribution uses deployment tooling (Compose env files/secrets, Kubernetes ConfigMaps/Secrets, systemd credentials, Ansible, pipelines). Secure Intake detects incompatibility but doesn't orchestrate configuration.

## 16. Audit

```text
configuration.validated | activated | reload_requested | reload_completed | reload_failed
secret.reference.changed
secret.rotation.started | completed | failed
deployment.profile.changed
deployment.identity_mismatch_detected
deployment.configuration_drift_detected
```

Include actor/automation identity, generation, changed setting paths, old/new non-secret fingerprints, validation result, activation time, affected roles. Never secret values. Changes made directly in files or external vaults are audited by Secure Intake only when detected/activated; the external provider audits the original action.

## 17. Data model

Only operational metadata is persisted — the DB is never a hidden second source of truth for infrastructure settings:

```text
deployment_configuration_generations
deployment_node_configuration_states
deployment_configuration_validation_runs
deployment_secret_rotation_events
deployment_configuration_audit_events
```

## 18. Operational UI

Read-only view for platform administrators: environment, profile, generation, providers, credential resolution status, signing key availability, node consistency. High-risk changes stay in deployment tooling and the CLI, not an ordinary web page.

## 19. Implementation order

1. Typed schema and source-generated serialisation.
2. Deterministic precedence.
3. Secret reference parser and provider abstraction.
4. Environment and protected-file providers.
5. Production/hardened validation policies.
6. Config CLI and preflight.
7. Local encrypted secret store.
8. Configuration generations and drift detection.
9. Secret rotation and controlled reload.
10. Optional external provider modules.
