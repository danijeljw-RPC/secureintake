# Review Note: Planning Pass of 2026-11-03

You answered Q-GEN-1 to Q-GEN-7 and delegated everything else. This note covers what was done, the calls most worth checking, and the actions only you can take. Allow about 30 minutes to read this note plus the four new documents.

## What was done

| Change | Where |
| --- | --- |
| All ~80 questions resolved | [QUESTIONS.md](QUESTIONS.md) (resolution register) · [DECISIONS.md](DECISIONS.md) P-01 to P-21 |
| Lost v1.0 to v1.7 foundations rebuilt | [00-foundations.md](architecture/00-foundations.md) |
| Lost end of v4.7 (quotas) planned | [04](architecture/04-resource-governance-and-quotas.md) §5, §9 and §15 to §21 |
| v1.0 scope and milestones | [25-implementation-roadmap.md](architecture/25-implementation-roadmap.md) |
| Crypto specs, suites and review plan | [26-cryptographic-specifications.md](architecture/26-cryptographic-specifications.md) |
| Every "illustrative" value replaced | [27-production-defaults.md](architecture/27-production-defaults.md) |
| Permission catalogue | [28-permission-catalogue.md](architecture/28-permission-catalogue.md) |
| Docs 02 to 24 | Each has a "Planning decisions applied" box. Routes, endpoints and CLI names are corrected inline |
| Sprag | Removed from the docs. Secure Intake is a clean-room build |

`notes.txt`, `sprag-source/` and the files at the repo root were **not changed**.

## The plan in one paragraph

v1.0 is a self-hosted, API-first, multi-tenant (shared tier) secure intake service. It has browser E2EE upload (hybrid post-quantum), CLI and browser review, signed receipts, RBAC with JIT and approvals, quotas, audit, and safe backup and restore operations. It needs about **32 weeks over five milestones (M0 to M4)** with 2 to 3 engineers. The critical path is the **external cryptography review**. Swoovr is the first test integrator at the end of M2: it creates an upload grant through the API, receives a webhook, and a reviewer decrypts the files with the CLI. Dedicated tenancy, custom domains, evidence packages, KMS/HSM and self-service are spread over v1.1 to v1.3.

## Calls worth checking (⚑ in QUESTIONS.md)

Each is defensible, but if you'd decide any of them differently, the rest of the plan changes.

1. **Hybrid post-quantum E2EE in v1 (Q-KEY-1).** X25519 + ML-KEM-768 protects long-lived evidence against harvest-now-decrypt-later attacks. The costs:
   - The browser ML-KEM library is a pure-JS dependency (`@noble/post-quantum`).
   - Linux hosts need OpenSSL 3.5+.
   - It's the hardest item for the reviewer.

   **Alternative:** ship X25519 only in v1 and add PQ as suite 2. That's lower risk and faster, but weaker long-term. The spec already allows either.
2. **v1.0 is fairly large (Q-GEN-6).** Built-in identity *and* OIDC, forms with field-level confidentiality, legal holds and backup fencing are all in v1.0. If you want something in front of users sooner, the cleanest cut is to **ship M2 as a private beta** for Swoovr only, with the Development profile and the crypto banner.
3. **Organisation Key Directory (Q-KEY-2).** It adds a signing step for the Security Officer whenever recipients change. It's optional in Production and required in Hardened. It's honest about its limits: malicious portal JavaScript can still skip the check.
4. **Recovery recipient required by default (Q-KCR-2).** This is safer against key loss, but it means an organisation key can decrypt everything. Some customers will want to opt out. Production allows that, with a recorded acknowledgement.
5. **No EF Core, hand-written SQL (00 F3).** Chosen for Native AOT and auditability. It means more code to write.
6. **React for the portal (Q-GEN-8).** Chosen because it's familiar. Preact or Svelte would give a smaller attack and dependency surface.
7. **Break-glass via FIDO2 keys and the host CLI (Q-BAK-1).** It needs physical security keys to be bought and held in sealed custody.
8. **Production servers on Linux only, macOS x64 dropped (Q-DEP-2).**

## Actions only you can take

| # | Action | Needed by | Why |
| --- | --- | --- | --- |
| 1 | **Select and contract an external crypto reviewer** (shortlist in [26](architecture/26-cryptographic-specifications.md) §8) | M0 week 2 | Critical path. Lead times are often 6 to 12 weeks |
| 2 | **Trademark and domain check on "Secure Intake"** | Before any public release | The name is generic. Renaming later is cheap in code, expensive in market |
| 3 | **Move `sprag-source/` out of this workspace** (and stop the `swoovr-sprag` test container in `docker-compose.yml` and `run.sh`) | Before M0 coding | It's GPL-3.0. Keeping it out of the working tree keeps the clean-room claim easy to defend. I didn't delete anything |
| 4 | Buy 2 or more hardware security keys for the **project release-signing key** and **break-glass** testing | M4 | Release signing (Q-DEP-3) and the emergency credential design |
| 5 | Set up a GitHub org and repo with **ghcr.io** and macOS, Windows and Linux CI runners | M0 | AOT builds need native runners for each target |
| 6 | Line up a **penetration test** for the portal and API | M4 | It's part of the v1.0 gate |
| 7 | Confirm Swoovr's integration needs (which API calls, webhooks and data it expects) | M2 | Makes Swoovr a real acceptance test for the API |

## Not covered by this pass

- **Threat model**, **spec texts (S1 to S10)** and **ADR folder**: these are M0 work items, not planning notes.
- The [swoovr-zammad-integration-review-prompt.md](../swoovr-zammad-integration-review-prompt.md) at the repo root wasn't touched. It's unrelated to Secure Intake.
- Estimates are for sequencing only. They aren't validated against a team or budget.
