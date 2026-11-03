# Cryptographic Key Governance

Source: notes v4.4. Builds on recipient models in [09-key-management-and-recipients.md](09-key-management-and-recipients.md) (v2.6/v2.7). Resolved questions: [QUESTIONS.md](../QUESTIONS.md) (Q-KEY-*). Planning decisions applied below take precedence over the source notes.

> **Planning decisions applied (2026-11-03)** — these override the text below where they differ. See [QUESTIONS.md](../QUESTIONS.md).
> - Suites: `SI-E2EE-1` (hybrid X25519 + ML-KEM-768) and `SI-SIG-1` (ES256) (Q-KEY-1).
> - Key substitution: **Organisation Key Directory**, required in Hardened (Q-KEY-2). Threshold recovery deferred (Q-KEY-3). All details are in [26](26-cryptographic-specifications.md).

## Decision

**Hybrid key governance**: built-in key lifecycle, policy and audit, supporting local protected keys, organisation-controlled E2EE keys and optional external KMS/HSM custody. **KMS/HSM is not mandatory for production**, though the deployment profile or organisation policy may require it for particular operations.

> Secure Intake manages key identities, public keys, authorisation, rotation and audit **without possessing the private keys that decrypt E2EE submissions.**

## 1. Key domains

Domains never share private keys.

| Domain | Private-key custodian | Purpose |
| --- | --- | --- |
| Receipt signing | Deployment or external KMS/HSM | Prove issuance of submission receipts |
| Evidence package signing | Deployment/export authority or KMS/HSM | Authenticate exported package manifests |
| Audit checkpoint signing | Deployment or KMS/HSM | Authenticate audit-chain checkpoints |
| Organisation/reviewer E2EE | Reviewers, organisation custody or external crypto client | Decrypt authorised submission content |
| Recovery keys | Organisation-designated custodians | Recover E2EE access when permitted |
| *Server secret encryption* (separate operational category) | Deployment | Protect server-managed secrets — not E2EE |

```text
Secure Intake key governance (registry · policy · lifecycle · approval · trust · audit)
   ├── Platform signing — local protected key or external signer — server MAY sign
   └── E2EE decryption — reviewer client or organisation custody — server CANNOT decrypt
Versioned trust and key history (fingerprint · epoch · status · provenance · revocation · signed events)
```

**An external KMS is not automatically E2EE.** If the server can call KMS decrypt and receive plaintext DEKs, server-blind E2EE is broken. Organisation-managed E2EE decryption must stay within an independently controlled client, HSM boundary or organisation-side service that never exposes plaintext to Secure Intake.

## 2. Key identity model

```csharp
public enum CryptographicKeyPurpose
{
    ReceiptSigning, EvidencePackageSigning, AuditCheckpointSigning,
    ReviewerEncryption, TeamEncryption, OrganisationEncryption, RecoveryEncryption,
    ServerSecretEncryption
}

public enum CryptographicKeyStatus { Pending, Active, Retiring, Retired, Revoked, Compromised, Destroyed }

public sealed record CryptographicKeyDescriptor(
    Guid KeyId, CryptographicKeyPurpose Purpose, Guid? OrganisationId,
    string AlgorithmSuite, string PublicKeyFingerprint,
    string CustodyProvider, string? ProviderKeyReference, long Epoch,
    CryptographicKeyStatus Status, DateTimeOffset CreatedAt,
    DateTimeOffset? ActivatedAt, DateTimeOffset? RetiredAt);
```

Symmetric server-secret keys use a separate descriptor with an opaque key-version ID (no fingerprint).

Invariants:

- A key ID is never reassigned to different material.
- Fingerprints derive from a canonical, algorithm-specific encoding.
- Algorithm suite, purpose and organisation ownership are immutable.
- Provider references are never proof of identity without verification.
- Historical key records remain available after retirement.

## 3. Signing-key lifecycle

```text
Pending → Active → Retiring → Retired
            ↘ Revoked   ↘ Compromised   (direct transitions allowed, e.g. Active → Compromised)
```

| State | Meaning |
| --- | --- |
| Pending | Generated/registered; awaiting validation and activation |
| Active | May sign new records |
| Retiring | Replacement active; controlled overlap if policy permits |
| Retired | No new signatures; historical verification supported |
| Revoked | Signing prohibited; trust treatment depends on reason and effective time |
| Compromised | Historical signatures need compromise-aware assessment |
| Destroyed | Custody disposition; never erases public key and lifecycle history |

### Planned rotation

1. Generate/register replacement. 2. Verify public identity and permitted algorithms. 3. Test signing against a non-production challenge. 4. Publish new public key as pending. 5. Obtain approvals. 6. Activate new signing epoch. 7. Stop signing with old key. 8. Retain old public key and trust history. 9. Retire/destroy old private material per custody policy.

Active key selection is via a versioned signing policy. Async jobs are **pinned** and never switch identity midway or silently downgrade algorithm:

```csharp
public sealed record SigningRequest(Guid OperationId, Guid KeyId, long KeyEpoch, string AlgorithmSuite, ReadOnlyMemory<byte> CanonicalPayload);
```

If the pinned key can no longer sign, the job is re-authorised or fails per operation policy.

## 4. Signing providers

```csharp
public interface ISigningKeyProvider
{
    string ProviderId { get; }
    ValueTask<SigningKeyCapabilities> GetCapabilitiesAsync(string keyReference, CancellationToken ct = default);
    ValueTask<SignatureResult> SignAsync(SigningOperation request, CancellationToken ct = default);
}

public sealed record SigningOperation(string KeyReference, string AlgorithmSuite, ReadOnlyMemory<byte> Payload, Guid OperationId);
public sealed record SignatureResult(string KeyReference, string AlgorithmSuite, byte[] Signature);
```

The caller verifies the returned signature against the expected public key and algorithm; providers can't silently substitute keys.

| Provider | Direction |
| --- | --- |
| Local protected key | Built-in |
| File-backed key with operator protection | Built-in |
| Azure Key Vault / Managed HSM | Optional |
| AWS KMS | Optional |
| PKCS#11 HSM | Optional |
| Other | Explicit compile-time module (AOT, no runtime plugins — [19](19-configuration-and-secrets.md)) |

### Local custody (production requirements)

Operator-controlled file permissions; encryption at rest; master key stored separately; no private keys in JSON config, logs or diagnostics; controlled export/backup; documented rotation and recovery. Never claim HSM-equivalent protection — a host with both encrypted key and unlock material can still sign.

## 5. Organisation and reviewer E2EE keys

The v2.7 hybrid recipient model is authoritative: individual reviewer, team, organisation and approved recovery public keys. The upload manifest records the exact recipient-key snapshot used.

```csharp
public sealed record RecipientKeyRegistration(
    Guid KeyId, Guid OrganisationId, Guid? PrincipalId, Guid? TeamId,
    string AlgorithmSuite, byte[] PublicKey, string Fingerprint, long Epoch, DateTimeOffset RegisteredAt);
```

- Prove private-key possession where practical; external custody uses authenticated attestation/PoP.
- An admin-submitted public key is **not** trusted just because the admin may edit key settings.

### Enrolment

```text
Generate key in trusted client → Submit public key + device metadata → Prove possession
→ Apply organisation key policy → Approve if required → Activate recipient key
```

Browser-generated keys depend on origin and delivered-JS integrity; high-assurance organisations can require independently distributed reviewer clients ([08](08-reviewer-clients.md)).

### Key substitution protection

A compromised server could swap recipient public keys at upload. Where assurance policy requires, the client validates the recipient manifest against an independently trusted identity or signed key directory. A signature verified with a key supplied by the same untrusted server doesn't help.

## 6. Recipient key rotation

| Operation | Result |
| --- | --- |
| Rotate for future submissions (**default**) | New uploads use new public key |
| Rewrap historical DEKs | An authorised client adds a new recipient envelope |
| Re-encrypt historical content | New ciphertext under a new DEK/protocol context |

Each rotation increments the key **epoch**; submissions retain the exact epoch used. Historical access is never granted merely by current team membership.

## 7. Revocation

| Kind | Effect |
| --- | --- |
| Access revocation | Blocks new ciphertext retrieval and operations |
| Recipient-key revocation | Key no longer selected for future uploads |
| Envelope replacement | Changes who can decrypt a newly published envelope-set version |
| Full re-encryption | New ciphertext, reducing future exposure from a compromised DEK/key |

None erase content an authorised recipient already copied.

When a reviewer loses access: revoke permissions → revoke sessions and grants → disable recipient key for future uploads → identify historical submissions encrypted to it → decide rewrap/re-encryption → audit. Shared team keys make member revocation hard (they may keep the private key) — prefer individual envelopes.

## 8. Compromise handling

| Compromised key | Response |
| --- | --- |
| Receipt signing | Stop signing, publish compromise status, assess historical receipts |
| Evidence package signing | Stop exports with key, review affected packages |
| Audit checkpoint signing | New trust epoch; assess checkpoint continuity |
| Reviewer E2EE | Revoke access, rotate, assess historical exposure |
| Organisation/team E2EE | Rotate custody, identify affected submissions, consider re-encryption |
| Recovery E2EE | Suspend recovery, rotate, assess affected envelopes |
| Server secret encryption | Rotate affected secrets and keys |

Rotation can't make a compromised key historically trustworthy; if compromised before a receipt time, the signature may not prove issuance. Independent timestamps and externally anchored checkpoints help, under their own trust assumptions.

```json
{ "incidentId": "keyincident_123", "keyId": "key_456", "status": "confirmed_compromise",
  "detectedAt": "2026-11-03T09:00:00Z", "suspectedCompromiseFrom": null,
  "actions": ["disable_signing", "activate_replacement", "publish_trust_update"], "reviewRequired": true }
```

An unknown compromise start is never invented; verification depending on it is **indeterminate**, not trusted.

## 9. Recovery-key custody

| Mode | Behaviour |
| --- | --- |
| `disabled` | No organisation recovery recipient |
| `organisation_key` | Recovery public key included in approved upload manifests |
| `external_custody` | Recovery private key held by an independent organisation-controlled service/custodian |

Recovery works only if a recovery envelope was included at encryption time or another authorised recipient can rewrap. **Enabling recovery later doesn't create access to historical submissions.**

Recovery requests specify: organisation and submission scope; reason; recovery key and epoch; requester; independent approvals; effective policy fingerprint; expiry and permitted output destination. The server authorises ciphertext retrieval and records activity but **never receives the recovered DEK or plaintext**; the organisation-side recovery client decrypts.

## 10. Key ceremonies

For: creating an organisation recovery key; replacing a compromised recovery key; activating a new receipt-signing authority; transferring KMS custody; destroying retired private material.

```csharp
public sealed record KeyCeremony(
    Guid CeremonyId, Guid KeyId, string CeremonyType, Guid? OrganisationId,
    string ProcedureVersion, string EvidenceDigest, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt);
```

Captures who authorised, which public key, which custody provider, and verification outcome — never private material, shares or passwords. Threshold multi-custodian schemes may come later using a **reviewed implementation**, never an in-house scheme.

## 11. Trust registries

Separate, non-interchangeable registries:

| Registry | Contains |
| --- | --- |
| Receipt issuer trust | Receipt-signing public keys and history |
| Evidence exporter trust | Package-signing public keys |
| Audit checkpoint trust | Audit-signing public keys |
| Organisation recipient directory | Approved E2EE recipient public keys |
| Recovery-key directory | Approved recovery public keys and epochs |

Public discovery:

```text
GET /.well-known/secure-intake/receipt-keys.json
GET /.well-known/secure-intake/evidence-signing-keys.json
GET /.well-known/secure-intake/audit-keys.json
```

Organisation recipient directories use authenticated organisation-scoped endpoints unless publication is intended.

Registry updates carry: monotonic sequence/epoch; canonical digest; previous-registry commitment; signing authority; effective time; status and revocation history. Offline verification needs an independently established trust anchor — downloading a registry and trusting its own keys proves nothing.

## 12. API

```text
GET  /api/platform/cryptographic-keys
POST /api/platform/cryptographic-keys
GET  /api/platform/cryptographic-keys/{keyId}
POST /api/platform/cryptographic-keys/{keyId}/activate
POST /api/platform/cryptographic-keys/{keyId}/retire
POST /api/platform/cryptographic-keys/{keyId}/revoke
POST /api/platform/cryptographic-keys/{keyId}/report-compromise

GET  /api/organisations/{id}/recipient-keys
POST /api/organisations/{id}/recipient-keys
POST /api/organisations/{id}/recipient-keys/{keyId}/rotate
POST /api/organisations/{id}/key-recovery-requests
GET  /api/organisations/{id}/key-recovery-requests/{requestId}
```

Registration and activation are separate where policy requires approval. **No endpoint returns stored private signing keys.**

## 13. CLI

```bash
secureintake keys list
secureintake keys inspect --key-id <key-id>
secureintake keys verify  --key-id <key-id>
secureintake keys rotate plan --purpose receipt-signing
secureintake trust verify --registry ./receipt-keys.json --trusted-key ./trust-anchor.pem
```

Uses the same policy and approval services as the API; running locally is not a bypass. The restricted recovery procedure is separate.

## 14. Dashboard

Desktop-only **Cryptographic Keys** page: purpose, algorithm, fingerprint, epoch, custody provider, status, trust history, incidents; private-key export shown as "Unavailable". Never shows private material. Operations require JIT elevation and approval.

## 15. Data model

```text
cryptographic_key_descriptors          cryptographic_trust_registries
cryptographic_key_epochs               cryptographic_trust_registry_versions
cryptographic_key_lifecycle_events     cryptographic_trust_decisions
cryptographic_key_provider_bindings    organisation_key_custody_policies
cryptographic_key_rotation_operations  organisation_recovery_key_assignments
cryptographic_key_compromise_incidents organisation_key_recovery_requests
cryptographic_key_ceremonies
```

Reuse existing recipient-key records where appropriate. Evidence keeps pointing at exact historical key ID and epoch; migrations never silently replace an envelope's key reference.

## 16. Audit events

```text
cryptographic.key.registered | activated | rotation_requested | rotated | retired | revoked | compromise_reported | destroyed
cryptographic.recipient_key.enrolled | revoked
cryptographic.envelope.rewrapped
cryptographic.content.reencrypted
cryptographic.recovery.requested | approved | completed | denied
cryptographic.trust_registry.updated
cryptographic.trust_decision.changed
cryptographic.key_ceremony.completed
```

Record key identity, epoch, purpose, custody provider, actor, authorisation reference, policy fingerprint, outcome. Never private keys, plaintext DEKs or decrypted content.

## 17. Implementation order

1. Unified key identity and lifecycle metadata.
2. Separate signing and E2EE governance interfaces.
3. Local protected signing-key provider.
4. Versioned public-key trust registries.
5. Receipt/evidence/audit signing-key rotation.
6. Recipient-key enrolment and proof of possession.
7. E2EE key epochs, revocation and historical access assessment.
8. Recovery custody policies and approval workflows.
9. Optional KMS/HSM signing providers.
10. Compromise handling, ceremonies, dashboard and audit.
