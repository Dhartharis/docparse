# Agent integrations and billing design

Date: 2026-11-11

Status: design for review; no runtime capability is introduced by this document.

Product source: [DocParse product definition](https://docs.google.com/document/d/1ao033DtGdlLEpfZSGLFziUoYwa5ztnBSh7mcjROCITE/edit), especially section 16. This document records implementation proposals and the subsequent chat decisions; it does not replace the commercial source of truth.

## Confirmed scope

- Develop the remaining agent, operational-data, billing and production capabilities incrementally, with a separate PR for each coherent change or correction.
- Support three integration modes: externally pushed updates, Pull when answering, and scheduled Pull synchronization. A connection may support more than one mode.
- The first Debt Assistant integration is read-only. Do not create sales, reserve stock, change debts or perform other external business writes.
- Preserve a standard contract usable by Debt Assistant and unrelated business systems.
- Keep WhatsApp, Zalo and Facebook/Messenger out of this delivery.
- Stripe is the proposed payment provider. Merchant availability, final prices and commercial policies remain to be confirmed before live activation.

Everything described below as a proposal is reviewable design, not an assertion that the user has approved every field, policy or implementation detail.

## Investigated baseline

DocParse V1 was inspected at commit `8322d074f40552ed34aff9080fb073d889a24bf3` on `codex/docs-local-v1`; its functional baseline is on `codex/local-v1`. At investigation time neither branch was integrated into `origin/main` (`f58f9a0f163ab826fecb5b4d846bc98525fbcfb3`). This documentation PR starts at main and does not include or integrate those branches.

- V1 is a .NET 8 modular monolith with Access, Knowledge, Bots, Chat and Usage, PostgreSQL/pgvector, private Blob files and a React client.
- `back/Modules/Chat/Application/QueryService.cs` retrieves authorized document passages and passes them to an answer generator. It has no operational-tool execution and returns early for bots with no knowledge bases.
- `back/Modules/Usage/Infrastructure/UsageMeter.cs` reserves and settles Ollama token measurements through Fyr Usage. Paid providers are rejected while metering is enabled. `channel` is recorded locally but is not currently included in the reserve request to Fyr Usage.
- The existing calculator accepts manual storage, retrieval, egress and processing forecasts. Those inputs are not live infrastructure measurements.
- Access uses ASP.NET Identity credentials and opaque bearer tokens; mail delivery is development-only. There is no Stripe integration.
- Debt Assistant uses Supabase identity and Account/Business ownership. Business, not Account, is the operational tenant; one account can own businesses with different currencies and countries. Billing currently uses RevenueCat.
- Debt Assistant has inventory schema models, including items, balances, presentations and movements. Schema presence does not prove a complete inventory/pricing API. That API is a dependency, not an existing integration.
- The Fyr portal is a static showcase, not an existing shared identity or application-connection broker.

Implementation PRs must first establish an integrated V1 base. Do not merge unrelated branches or change Debt Assistant as part of this design PR.

## Ownership and application boundaries

Proposed responsibilities inside DocParse's existing deployable:

| Owner | Responsibility |
| --- | --- |
| Access | Current actor, organization membership and administrator authorization |
| Integrations (new) | Connections, external business mappings, credentials, consent scopes, provider HTTP adapters, inbound receipts and scheduled synchronization |
| OperationalData (new) | Queryable product offers/availability, source versions, freshness and publication policy |
| Bots | Explicit connection/tool grants and which audience may use them |
| Chat | Select authorized tools, validate arguments/results, combine evidence and produce the answer |
| Usage | Reservations, measurements and settlement through Fyr Usage |
| Billing (new) | Organization subscriptions, payment-provider references, invoice reconciliation and paid entitlements |

The two new operational modules separate transport/connection lifecycle from business data. Review that boundary before implementation; do not add extra projects, services, a general workflow engine, arbitrary script execution or a generic plugin runtime.

Modules expose only small Contracts capabilities. Debt Assistant remains authoritative for its business data; DocParse stores only an authorized projection. No cross-application database access, shared passwords or inferred organization mapping by email.

## Standard operational contract: proposed v1

Publish an OpenAPI contract, JSON examples and provider conformance fixtures in DocParse before implementing adapters. Both Push and Pull use the same representation. Version the transport contract independently from document embeddings and provider-specific schemas.

### Connection identity and access

- A connection belongs to one DocParse organization and maps explicitly to one external business. A source record is identified by connection plus stable external record ID; SKU is an attribute, not a globally unique key.
- Use separate server credentials for receiving updates and querying a provider, limited to a connection and required scopes. Existing conversation API keys cannot update or read operational integrations.
- Proposed scopes: `catalog.read`, `prices.read`, `availability.read`, and `operational-data.push`. Reject tenant IDs that disagree with authenticated connection scope.
- Require encrypted secret storage, rotation, revocation and redacted diagnostics. Push uses signed requests covering the raw body, timestamp and delivery ID, with bounded replay acceptance and durable duplicate detection. Final signing details belong in the contract PR.
- Restrict outbound provider destinations and redirects to prevent access to private infrastructure. Local-test exceptions must be explicit and unavailable in production.

### Product offer and availability representation

| Field | Proposed meaning |
| --- | --- |
| `externalId` | Stable source identifier for a specific sellable presentation/variant |
| `productId`, `sku`, `name` | Source product identity, optional SKU, display name |
| `presentation`, `unitCode` | Exact sellable unit; do not confuse base-unit inventory with pack availability |
| `price.amount`, `price.currency` | Exact decimal string and currency; explicit unknown/unavailable state, never implicit zero |
| `availability.available`, `availability.reserved` | Decimal quantities in the declared unit, each explicitly known or unknown |
| `availability.tracking` | Whether stock is tracked; untracked does not mean zero or unlimited |
| `version` | Monotonically increasing version per source record |
| `observedAt`, `validUntil` | UTC observation and expiry; receiving a stale update does not refresh its observation time |
| `active` | Whether the offer may still be used; removal also supports a versioned tombstone |

Available quantity is the source's authoritative sellable quantity. DocParse must not subtract reserved quantity again. Do not convert currencies, round quantities to integers, or manufacture SKU/price/reservation values absent from the source. Debt Assistant's eventual adapter must explicitly map presentations, balances and price semantics.

Connection configuration defines freshness limits separately for price and availability. Effective expiry is the earlier of the source's valid-until and the configured maximum age. Exact defaults and source clock/version guarantees remain contract-review decisions.

### Push receipt and ingestion

Proposed route: `POST /api/integrations/v1/connections/{connectionId}/events`.

- Envelope: contract version, event ID, event type, source timestamp and versioned record payload. Start with absolute upserts and tombstones, not stock deltas.
- Authenticate, validate, and persist an inbox receipt before acknowledging acceptance. Return a receipt/status resource; acceptance does not promise completed projection or delivery to a user.
- A repeated event ID with the same payload is a duplicate; the same ID with a different payload is a conflict. Scope uniqueness by connection. Older record versions cannot overwrite newer ones.
- Claim/lease receipt processing atomically in a short transaction, commit, perform external work if needed, and persist the result afterward. Retry/lease state survives restarts.
- Preserve tombstone ordering to stop delayed events from resurrecting deleted records. Revocation makes the connection unusable immediately, including cached records.

### Pull on demand

Proposed provider capability: `POST /integrations/v1/operational-data/query` with bounded product IDs/SKUs or a search query and explicit requested fields. It is a read-only query despite using POST for the structured request.

- The server resolves the permitted connection and scope before the agent can request data. The model cannot choose arbitrary URLs, tenant IDs or secrets.
- Return the shared v1 representation, explicit not-found/unavailable results, source timestamps and versions. Search results must identify ambiguity rather than silently select the wrong presentation.
- Use bounded requests, timeouts and cancellation. Price/stock claims require fresh results. A failed Pull may use the projection only when it is still fresh and the connection policy allows that fallback.
- When neither source is fresh, answer that price/availability cannot be confirmed. Do not use an old chat answer or document embedding as substitute evidence.

### Scheduled Pull

Proposed provider capability: `GET /integrations/v1/operational-data/changes?cursor=...` returning bounded pages, upserts/tombstones and a continuation cursor.

- Support initial synchronization and incremental changes. The provider defines a stable snapshot/watermark so changes during pagination cannot disappear.
- Persist the projection and next cursor atomically after a page succeeds. Retrying a page is safe. A failed page must not advance the cursor.
- An expired cursor triggers a controlled full resynchronization. Absence from a partial page never means deletion; full snapshots need an explicit completion marker before absence can be reconciled.
- Persist schedules, claims, due times and failures. Push and both Pull modes resolve conflicts with the same record-version rules; sources without comparable versions require an explicit precedence policy before mixing modes.

## Agent behavior and permission model

Start with explicit read-only tools such as product search, offer lookup and availability lookup, not arbitrary HTTP execution. A document-free bot with authorized operational tools must work; remove the current unconditional no-knowledge-bases exit only in the relevant Chat PR.

Resolve organization, bot, connection, audience, field publication policy and actor authorization server-side on every tool call. Deny by default. Only explicitly published product fields may be exposed through a public bot. Internal sources require a verified authorized actor; an external conversation API key does not itself identify an employee.

Tool inputs and external results are untrusted data. Validate schemas, cap calls/result sizes and reject instructions embedded in product text. Include provenance and observation time in operational evidence. Retrieved documents keep document/page citations; operational evidence identifies its authorized source without leaking internal connection details.

Revocation invalidates new queries and cached access. All channels use the same authorization and orchestration. Record tool execution metadata, policy decisions and failure codes without logging provider secrets or unrestricted personal data.

## Debt Assistant connection experience

Target flow: **Connect Debt Assistant -> choose business -> authorize data -> assign assistants -> test connection**.

Proposed behind-the-scenes flow:

1. An administrator starts connection setup in DocParse.
2. Debt Assistant authenticates the person and verifies their right to authorize the selected business and scopes.
3. A short-lived, single-use authorization exchange binds the initiating DocParse organization to that business. Validate initiation state and an allowlisted callback; do not place reusable credentials in browser URLs.
4. Servers exchange the scoped connection credentials. Backfill and recurring sync can start; the UI reports progress and failures.
5. The administrator grants specific assistants access and explicitly chooses public fields. Disconnect revokes the authorization and immediately excludes synchronized data from use.

This is a proposed delegated connection protocol, not a claim that either application already implements OAuth or SSO. It requires a separate cross-product design and Debt Assistant PRs after its modules/APIs are ready. Shared login, common billing and account merging are not implied by connecting a business. Other providers can use manually configured credentials and the same data contract.

## Billing and commercial boundaries

Propose one DocParse subscription per organization, with one payment-provider customer mapping. Debt Assistant's RevenueCat billing remains separate. Paid access must follow verified server-side payment/subscription state, not a browser success redirect or a manual plan picker.

- Stripe Checkout initiates subscription payment. The customer portal manages supported payment details, invoices and cancellation.
- Validate webhook signatures over the original body; durably store unique provider events before asynchronous processing. Handle duplicates, out-of-order notifications and reconciliation against current provider state.
- Billing synchronizes valid entitlements/cycle boundaries into Fyr Usage. Fyr Usage remains the only authority calculating consumed credits; Stripe must not introduce another token-to-credit formula.
- Unlimited bills only finalized excess consumption with stable settlement identities and reconciliation. Late settlement, invoice finalization, cancellation, refunds, disputes and failed charges require explicit policy before live activation.
- Existing V1 next-cycle plan selection is a local simulation, not proof of payment. Integrating Stripe must replace that authority without resetting allowance or granting unpaid upgrades.
- Add payment fees and attributable operational costs to unit-economics scenarios. Do not freeze tariffs or claim the <=50% cost constraint without measured assumptions and heavy-use validation.
- Use test mode and deterministic provider doubles first. No live charges, merchant onboarding or resource deployment from this design PR.

Open commercial decisions: merchant country/entity, supported billing currencies, final credit value/plan allowances, billing anchor alignment (V1 uses calendar cycles), upgrade/downgrade and proration policy, trial policy if any, grace/collection rules for failed payments, Unlimited notifications/spending policy, tax presentation, refund/dispute handling and excess-usage invoice mechanism. Existing product promises such as Unlimited continuity must remain visible when resolving these policies.

Official references checked during investigation:

- [Stripe Checkout](https://stripe.com/payments/checkout)
- [Customer portal](https://docs.stripe.com/customer-management)
- [Webhook processing](https://docs.stripe.com/webhooks)
- [Usage-based billing options](https://docs.stripe.com/billing/subscriptions/usage-based)
- [Merchant availability](https://stripe.com/global)

Do not select a new usage-pricing engine merely because a provider offers one. Review the current Stripe options against Fyr Usage's existing ownership and the desired invoice workflow in the billing design PR.

## Validation and rollout

Use disposable tenant data, a deterministic operational-provider double and payment test mode. No production credentials or real charges in automated tests.

Acceptance must cover cross-tenant access; connection/field/tool grants; public versus internal data; spoofed identity; signature/replay rejection; duplicate/conflicting/out-of-order updates; unknown price/stock and presentation units; tombstones; cursor recovery; lease recovery after restart; concurrent Push/Pull; controlled-time expiry; stale-data failures; bounded tool loops; duplicate payment notifications and excess-settlement reconciliation.

Each vertical implementation PR includes schema, backend, frontend/localization where user-facing, relevant automated checks and rendered desktop/mobile review. Keep external I/O outside database transactions. Required build/lint/test commands come from the integrated V1 repository instructions. A documentation-only PR checks links/structure and `git diff --check`; it does not rerun or claim runtime validation.

See [delivery plan](agent-integrations-delivery.md) for sequencing, dependencies and decision gates.
