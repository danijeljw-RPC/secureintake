# Public Upload Portal: Architecture and Distribution

Source: notes v2.4. Resolved questions: [QUESTIONS.md](../QUESTIONS.md) (Q-PRT-*). Planning decisions applied below take precedence over the source notes.

> **Planning decisions applied (2026-11-03)** — these override the text below where they differ. See [QUESTIONS.md](../QUESTIONS.md).
> - Stack: **React, Vite, TypeScript and Tailwind** with a strict dependency budget (Q-GEN-8).
> - API version header: `SecureIntake-Api-Version: YYYY-MM` (Q-PRT-1).
> - Crypto libraries: WebCrypto plus pinned, vendored `@noble/post-quantum` (Q-PRT-3, [26](26-cryptographic-specifications.md)).
> - Integrated hosting in v1.0. Standalone static hosting in **v1.1**.

## Decision

The public upload portal is **built once** and distributed two ways from the **same release artifact**:

1. **Integrated** — served by the Secure Intake application host.
2. **Standalone static** — published to any compatible static host.

Same source, encryption implementation, upload workflow and API contracts. Hosting mode never changes security guarantees, and the artifact is never rebuilt per organisation.

## 1. Frontend architecture

Lightweight **TypeScript** portal with a dedicated cryptography module, independent of the admin frontend's stack. Backend: **.NET 10 Native AOT**. The portal runs on plain static hosting without a .NET runtime.

```text
secure-intake/
├── src/
│   ├── SecureIntake.Api/          ├── SecureIntake.Application/
│   ├── SecureIntake.Host/         ├── SecureIntake.Domain/
│   ├── SecureIntake.Worker/       ├── SecureIntake.Contracts/
│   ├── SecureIntake.Cli/          └── SecureIntake.Cryptography/
├── frontend/
│   ├── portal/src/{app,components,pages,upload,crypto,api,branding,security}
│   └── admin/
├── packages/{api-client,crypto-protocol,shared-types}
└── tests/{interoperability,frontend-security,end-to-end}
```

Browser cryptography must interoperate with independently distributed reviewer and automation clients ([08](08-reviewer-clients.md)) and never depend on the server holding private keys.

## 2. Topology

```text
Integrated:   one host (ASP.NET Core) → static portal + API
Separate:     static host/CDN → HTTPS → independent .NET API host
              (same frontend artifact · identical encryption code · upload workflow · API client)
```

**Preferred production: same-origin**, even on separate infrastructure, via reverse proxy:

```text
https://documents.example.org/      → static frontend
https://documents.example.org/api/  → Secure Intake API
```

Cross-origin API is an explicit supported configuration, not the default.

### Integrated hosting

Frontend compiled to static assets at release build; served by the .NET host without Node.js at runtime. Initially a **versioned asset directory** next to the executable (simpler updates, debugging, hash verification) rather than embedded resources. Executable and assets form a compatible release set; the host verifies the frontend release manifest at startup/preflight.

```text
secure-intake/
├── secureintake
├── appsettings.json
└── wwwroot/{index.html, assets/app.a8f42c.js, assets/crypto.9b73e1.js, assets/app.4c28ab.css, release-manifest.json}
```

### Static hosting

Cloudflare Pages, Azure Static Web Apps, S3 + CDN, Nginx/Caddy, any HTTPS static server — operator-provisioned. Output: `dist/{index.html, assets/*, release-manifest.json, integrity-manifest.json}`. **Everything in static assets is public**: no DB/storage credentials, API secrets or private keys.

## 3. Runtime bootstrap

One common build loads tenant presentation config at runtime:

```text
GET /api/public/portal/bootstrap
```

The API resolves the organisation from the validated hostname or an authorised platform-domain route ([06](06-branding-and-custom-domains.md)).

```json
{
  "organisation": { "displayName": "Example Organisation", "logoUrl": "/assets/branding/logo.webp",
                    "theme": { "primary": "#274C77", "appearance": "system" } },
  "portal": { "locale": "en-AU", "features": { "returnAccess": false } },
  "protocol": { "version": 1, "minimumClientVersion": "1.4.0" }
}
```

Only public presentation settings and non-secret protocol metadata. Recipient keys and suite details come from a **separate** authenticated or capability-scoped endpoint with explicit key-authenticity controls. A compromised host's bootstrap can't establish trust in keys it returns.

## 4. API communication

Generated TypeScript client from the .NET OpenAPI contract (reduces drift).

```ts
export class SecureIntakeApiClient {
  constructor(private readonly options: { baseUrl: string; fetch?: typeof fetch }) {}
  async getPortalBootstrap(signal?: AbortSignal) {
    const response = await (this.options.fetch ?? fetch)(
      new URL("/api/public/portal/bootstrap", this.options.baseUrl),
      { method: "GET", credentials: "same-origin", signal });
    if (!response.ok) throw new Error(`Portal bootstrap failed: ${response.status}`);
    return response.json();
  }
}
```

Cross-origin credential behaviour is designed explicitly — never a blanket `credentials: "include"`. Short-lived upload capabilities are held **in memory**, not persistent browser storage.

## 5. Upload workflow (identical in both modes)

1. **Resolve portal** — branding, dropbox policy, protocol compatibility.
2. **Verify access** — PIN, email, OTP or OIDC as required.
3. **Validate recipients** — approved suite and authenticated recipient keys.
4. **Encrypt locally** — generate file key, encrypt chunks in the browser.
5. **Upload ciphertext** — scoped upload authorisation.
6. **Finalise** — server verifies manifest and ciphertext metadata.
7. **Issue receipt** — reference plus permitted status/return capabilities ([15](15-receipts.md)).

## 6. Version compatibility

| Component | Purpose |
| --- | --- |
| Application release | Overall product |
| API contract | Request/response compatibility (date-based) |
| Upload protocol | Session and finalisation semantics |
| Encryption protocol | Ciphertext and envelope compatibility |
| Frontend build | Static asset release |
| Branding schema | Presentation configuration |

No `/v1` routes; compatibility via headers and capability metadata ([20](20-deployment-packaging-and-upgrades.md)):

```json
{ "api": { "current": "2026-11-03", "minimumSupported": "2026-08-01" },
  "uploadProtocol": { "current": 2, "supported": [1, 2] },
  "encryptionProtocol": { "supported": [1] } }
```

- The frontend checks compatibility **before starting an upload and before finalisation**; incompatible builds show a clear update requirement.
- The API supports the active frontend and any still-valid sessions from older supported releases (bounded window), or terminates them safely.
- New deploys: new page loads get the latest `index.html` and changed hashed assets only; open tabs keep their loaded code. **Never force-reload during encryption/upload.**

## 7. Caching

| Resource | Policy |
| --- | --- |
| Hashed JS/CSS | `public, max-age=31536000, immutable` |
| `index.html` | `no-cache` (revalidate every navigation) |
| Release manifest | No-cache or short revalidation |
| Branding assets | Versioned, cacheable |
| Portal bootstrap | Short-lived or `no-store` where sensitive |
| Recipient keys | `no-store` unless a verified cache protocol protects them |
| Upload sessions, auth responses, receipts | `no-store` |

**No service worker** in the initial release (lifecycle/integrity concerns for crypto JS). Offline upload not required for v1.

## 8. Content Security Policy

Template for same-origin (not final):

```text
Content-Security-Policy:
  default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:;
  connect-src 'self'; font-src 'self'; object-src 'none'; base-uri 'none';
  form-action 'self'; frame-ancestors 'none'; upgrade-insecure-requests
```

- Direct-to-storage uploads add the explicitly authorised storage endpoints to `connect-src`; separate frontends add the configured API origin. **No broad wildcards** (e.g. `https:`).
- Fix the build rather than adding `'unsafe-inline'`. No organisation-supplied scripts.

Other headers: `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `Permissions-Policy: camera=(), microphone=(), geolocation=()`, `Cross-Origin-Resource-Policy: same-origin`. HSTS on production origins (`includeSubDomains` only when the operator controls the whole hierarchy). COOP/COEP only after validating compatibility with storage uploads, auth redirects and browser crypto.

## 9. Encryption integrity (most important)

The server never sees plaintext or private keys, but the browser runs JS from the hosting environment. A compromised static host, CDN, DNS or pipeline can replace it. **Client-side encryption doesn't protect against a compromised frontend delivery environment.**

Required controls: reproducible/independently verifiable builds where practical; signed release manifests; content-hashed assets; dependency pinning and lockfile verification; SBOM; CI security scanning; strict CSP; no third-party runtime scripts; independent cryptographic interoperability tests; explicit recipient-key authentication; release provenance and deployment verification.

SRI doesn't help if the attacker also controls the HTML; a signed manifest checked by JS from the same compromised origin isn't independent trust. High-assurance installations: **independently distributed upload client** or an independently trusted verification mechanism.

## 10. Encryption module

Small versioned application-level interface (not a protocol spec):

```ts
export interface EncryptionRecipient { keyId: string; algorithm: string; publicKey: Uint8Array; }
export interface EncryptionManifest {
  protocolVersion: number; suiteId: string; fileId: string;
  chunkCount: number; ciphertextLength: number; manifestDigest: Uint8Array;
}
export interface FileEncryptionResult { manifest: EncryptionManifest; encryptedChunks: AsyncIterable<Uint8Array>; }
export interface FileEncryptor {
  encrypt(file: File, recipients: readonly EncryptionRecipient[], signal?: AbortSignal): Promise<FileEncryptionResult>;
}
```

- Reviewed constructions and vetted libraries only. **No custom hybrid post-quantum construction without independent review.**
- Stream large files incrementally (never load multi-GB files into memory); browser streams and Web Workers with bounded buffers and backpressure. Workers help responsiveness but are not a security boundary.

## 11. Direct-to-storage uploads

```text
Browser → request upload session → API → scoped upload authorisation
Browser: encrypt chunks locally → upload ciphertext → object storage
Browser → finalise → API → verify stored ciphertext
```

- Short-lived, tightly scoped authorisation; never long-lived storage credentials in the browser.
- Operator configures storage CORS for approved portal origins, methods and headers.
- The API verifies object size, identity, session and authenticated manifest before committing. A client checksum alone isn't evidence.

## 12. Static hosting and custom domains

The domain registry stays authoritative. Integrated hosting resolves the hostname directly; for static hosting the operator serves the right portal per hostname. The frontend never infers organisation from a client-supplied tenant ID; the API validates hostname→organisation. For different origins, the API holds an explicit trusted-frontend-origin → verified-domain mapping. **CORS is not authorisation.**

## 13. Deployment validation

```bash
secureintake frontend verify --manifest ./dist/release-manifest.json
secureintake frontend compatibility --api https://api.example.com
secureintake frontend security-check --url https://documents.example.org
```

Checks: manifest and hashes; API compatibility; HTTPS/certificate; security headers; CSP; expected asset versions; trusted API/storage origins; custom-domain routing; no development-only config. Evidence at time of test, not a guarantee against later compromise.

## 14. Implementation order

1. TypeScript portal and shared API client.
2. Integrated static asset hosting.
3. Separate static build packaging.
4. Runtime bootstrap and branding.
5. Upload protocol compatibility negotiation.
6. Streaming encryption module and interoperability tests.
7. Direct-to-storage ciphertext uploads.
8. CSP and browser security policy.
9. Release manifests and integrity verification.
10. Deployment preflight and update-compatibility tests.
