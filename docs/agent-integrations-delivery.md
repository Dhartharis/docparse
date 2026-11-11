# Agent integrations delivery plan

Date: 2026-11-11. Status: proposed PR sequence, not implemented functionality.

Design: [agent integrations and billing](agent-integrations-design.md). Product decisions remain in the linked product definition and the user's subsequent confirmed instructions.

## Delivery rules

- One coherent feature, correction or documentation change per branch and PR. No merges without explicit integration authorization.
- Base each implementation on the then-current integrated main. Preserve unrelated work. Do not silently pull the complete local V1 into a feature PR.
- The first design PR can target current main independently. Runtime PRs require integration of the existing `codex/local-v1` and `codex/docs-local-v1` work (or an explicitly agreed alternate base).
- Implement database/backend/frontend together for user-facing features, with validation and EN/ES/VI catalogs. Split an entry further when needed for reviewability; this table does not mandate large PRs.
- Confirm material open design policies before their dependent code. Other independent work may continue.
- WhatsApp, Zalo and Facebook/Messenger are deferred. External business writes are excluded from the first integration.

## PR sequence and acceptance

| PR scope | Dependencies | Reviewable outcome and required acceptance |
| --- | --- | --- |
| 1. Scope and architecture design | None | This design, confirmed modes/read-only scope, proposals and explicit unresolved decisions; no runtime changes |
| 2. Operational v1 contract | V1 base, design review | OpenAPI and examples for Push, query, changes and receipts; field/unit/unknown-value semantics; authentication/signing, versioning, expiry, pagination and error guarantees; conformance fixtures |
| 3. Connection management | Contract | Admin-only creation/configuration, credential protection/rotation/revocation and tenant mapping; UI plus authorization tests; generic providers first |
| 4. Operational projection and Push | Connections | Initial/updated offers, inbox receipts/status, durable ingestion, version/tombstone ordering, data visibility; duplicates, conflicts, isolation and restart tests |
| 5. Pull during queries | Contract, connections, projection policy | Read-only provider adapter with bounded query, timeout, destination restrictions, validation and explicit freshness/fallback rules; deterministic provider tests |
| 6. Scheduled Pull | Contract, connections, projection | Initial/import and incremental sync, durable schedule/cursor/retries, snapshot completion and resync; crash/pagination/concurrent-mode tests and sync-status UI |
| 7. Bot tool grants and audiences | Connections, operational contract | Explicit tools/connections/public fields per bot; internal actor verification and no elevation through API/widget; management UI and isolation tests |
| 8. Agent operational queries | Pull/Push availability, tool grants | Product search/price/stock tools alongside RAG; document-free operational bots; ambiguity, unknown/stale/failure handling and bounded model execution; browser journeys |
| 9. Operational usage accounting | Contract, Fyr Usage contract review | Real tool/channel/storage measurements, reservation/settlement and durable reconciliation; no duplicate credit formula; coordinate separate Fyr Usage PRs where required |
| 10. Debt Assistant connection design | v1 contract, Debt Assistant readiness review | Agreed delegated authorization protocol, Business mapping, permissions, revocation/deletion, schema adapter and conformance plan; no assumption of shared identity |
| 11. Debt Assistant provider and connector | Its operational modules/APIs ready, connection design | Separate provider PRs in Debt Assistant and consumer PRs in DocParse; user selects/authorizes business without copying credentials; cross-application tests |
| 12. Billing lifecycle design | Merchant/commercial decisions | Subscription/cycle source of truth, upgrades/downgrades, cancellation, late excess usage, failed payments, refunds/disputes, invoice method and migration from simulation |
| 13. Stripe subscription checkout | Billing design, test configuration | Admin checkout, organization/customer association and server-verified activation; duplicate subscription protection and deterministic tests |
| 14. Stripe lifecycle reconciliation | Subscription integration | Signed webhook inbox, out-of-order recovery, durable retries, paid entitlement/cycle sync with Fyr Usage; payment failure/cancellation/refund/dispute cases |
| 15. Billing self-service | Lifecycle reconciliation | In-app subscription/invoice status and customer-portal access limited to authorized admins; supported plan/cancellation operations match approved policies |
| 16. Unlimited excess billing | Final tariffs, billing lifecycle, finalized Usage metrics | Auditable excess-to-invoice export with stable identities, rounding, retries and late-event policy; no duplicate charge or allowance reset; user-visible estimate and approved alerts |
| 17. Unit economics and commercial configuration | Actual infrastructure/provider/payment costs | Update existing calculator, validate light/normal/heavy scenarios and Free budget, publish approved tariffs/allowances through Fyr Usage; no fabricated GPU costs |
| 18. Paid AI adapter measurement | Provider/model selection | Complete usage extraction, reservations and error accounting before enabling each adapter; provider-specific PRs and controlled tests |
| 19. Production email | Provider/region selection | Replace development-only transport, secure configuration and delivery diagnostics; preserve outbox semantics and provider acknowledgement limits |
| 20. Commercial landing | Offer/copy and hosting agreement | Explain product, interactive demonstration and registration/login; coordinate a separate Fyr portal PR if required; verify actual domains/path rather than assuming the older document URL matches hosting |
| 21. Production preparation | Hosting/security/cost choices | Deployment/migration procedure, HTTPS/origins/proxy rules, secrets/key persistence, observability, retention, restore-tested backups and operating instructions; no automatic publication |
| 22. Production acceptance and release | Relevant feature validation and explicit deployment authorization | Environment-specific acceptance including delivery, recovery, payments and cloud quotas/costs; publish only when explicitly authorized |

PRs 13-15 may require a different split after billing lifecycle review so every accepted commit remains safe and paid access is never activated from an incomplete event handler. Financial approval and infrastructure readiness gate live operation, not writing testable adapters.

## Other pending items and boundaries

- External identity/Entra is conditional, not an already selected replacement. Connecting products does not authorize an identity migration. Select SSO only through a separate design if wanted.
- Durable chat history is absent today but is not a newly confirmed requirement in this scope. Decide retention/privacy and user value before adding it.
- Review the current Excel export/EPPlus license before commercial use; changing/removing/replacing that export requires a separate scoped PR if necessary.
- Do not sweep unknown historical shared Blob objects. Preserve the ownership constraints documented by V1.
- Review outstanding development-dependency advisories in Fyr Usage through its own repository/PR; do not bundle test-runner upgrades into DocParse features.
- External mutations and human-confirmed workflows need a later explicit design and authorization. Read-only synchronization writes to DocParse's projection do not authorize operational writes in Debt Assistant.
- No actual cloud costs, payment availability, email delivery or production security are certified by local tests or this document.

## Open decisions before dependent implementation

1. Approve the proposed module boundaries and standard operational contract semantics, especially units, record ordering, signing and initial/full synchronization.
2. Set allowed freshness/fallback policies and the public/internal field publication model.
3. Agree the cross-product connection/authorization protocol once Debt Assistant's operational APIs are designed.
4. Confirm merchant entity/country and Stripe suitability; finalize the billing lifecycle and tariffs before live collection.
5. Select actual production providers, environments and deployment responsibilities. No Entra migration or Azure publication is implied.

The confirmed decisions are Push plus both Pull modes, read-only first integration, separate PRs and deferred social channels. Other choices in this plan remain proposals for review, not silently finalized product policy.
