# Operational data v1: proposed integration contract

Status: **draft for review, not an implemented API**. This PR advances the contract step of [design PR #4](https://github.com/Dhartharis/docparse/pull/4); it does not approve or implement that entire roadmap. The V1 application and Fyr Usage dependency are now integrated into main. No Debt Assistant, payment, cloud or shared-skill files are changed here.

The confirmed scope is Push, on-demand Pull, scheduled Pull and a read-only first integration. The authentication, field and ordering rules below are concrete proposals for review before runtime implementation. Both applications must conform to the same version rather than maintain different copies.

## Artifacts and validation

- [OpenAPI](openapi.json) defines receiver versus provider operations, required authentication and transport responses.
- [JSON Schema](schema.json) is the sole definition of payload shapes, referenced by OpenAPI. Decimal values and revisions are strings so JavaScript clients do not lose precision.
- [Examples](examples) contain synthetic, non-customer data. Their historical timestamps are intentionally fixed; they are payload examples, not currently valid signed requests.
- [Behavior cases](behavior-cases.json) specify expected lifecycle outcomes for future receiver/provider tests. They do not claim those features work today.
- [Authentication vectors](authentication-vectors.json) give exact bytes and signatures for implementing the signing algorithm in different languages. The included key is public fixture data, never an application credential.

From the repository root:

```powershell
python -m pip install -r tests/requirements-contracts.txt
python tests/operational_contract.py
```

The checks validate schemas, local references, payload examples, negative serialization cases and authentication vectors offline. They do **not** exercise tenant authorization, durable storage, retries or provider networking. Those require later application/provider conformance tests. The extra Python dependency is for contract checks only; application dependencies are unchanged.

The files use [OpenAPI 3.1.1](https://spec.openapis.org/oas/v3.1.1.html) and JSON Schema 2020-12. Format validation is enabled explicitly in [python-jsonschema](https://python-jsonschema.readthedocs.io/en/stable/validate/). No test retrieves schemas or calls providers over the network.

## Data identity, money and availability

A connection maps one DocParse organization to one provider business. Every credential is scoped to that connection, direction and business. `sourceBusinessId` must equal the authenticated mapping; a supplied ID cannot select a different tenant. Resolve this before data access. Return 404 for a receipt/connection outside the credential's scope without disclosing whether it exists.

`externalId` identifies a sellable presentation, not just a generic product. `(connection, externalId)` is the projection identity. SKU is optional and not necessarily unique; never use it as the database uniqueness key. Product IDs, SKU, unit and presentation must be mapped explicitly by the provider. Do not infer missing values from an item's name.

- Price is the amount for one declared presentation/unit, with an explicit currency and tax treatment. No exchange-rate conversion or implicit tax addition.
- Quantities are exact decimals in that unit. Available means the source's sellable quantity; do not subtract `reserved` again. A known zero is meaningful. An unknown value is an object with `state=unknown`, not zero, null, infinity or a missing property.
- Untracked/unknown inventory cannot contain known quantities. A price or quantity outside the supported non-negative decimal range must return unknown or a provider error; do not clamp or round an invalid value into apparently confirmed stock.
- Unknown reserved quantity must not prevent reporting a fresh known available quantity. No reservation/sale/debt write operation exists in this contract.
- `active=false` excludes an offer from answers; it does not erase its version. Tombstones exclude deleted offers and prevent stale resurrection.
- Only requested and authorized fields may be populated. Missing requested scopes fail with 403; unrequested price/availability values are unknown with `reason=not_requested`. Public-bot field publication is a separate DocParse authorization layer, never inferred from the connection credential.

All dates are valid UTC instants ending in Z. Schema validation checks date shape and validity; application validation must also require `validUntil >= observedAt` and reject future observation/update/deletion times beyond the allowed source clock skew. Connection setup must choose positive freshness limits for price and stock and a source clock-skew allowance before enabling a bot; no production defaults are silently chosen in this contract.

Freshness is evaluated separately for price and availability: `now < min(validUntil, observedAt + configuredMaxAge)`. At the exact expiry instant, the value is stale. Receipt time, retry time and a new event ID never extend freshness. Stale/failed live lookup may use the projection only if its relevant field is still fresh and the administrator enabled that fallback. Otherwise the answer must say the value cannot be confirmed. Documents or previous chat replies cannot supply a substitute price/stock claim.

## Comparable source revisions

`version` is a positive integer string, up to 38 digits. Compare numerically, never lexicographically or via IEEE floating point. One provider business must issue a comparable committed revision sequence for **all** business changes returned through Push, query and the change feed. Multiple records changed atomically may share a revision. A published synchronized record must receive a newer revision whenever its representation changes, including published freshness metadata. An adapter without this guarantee cannot safely enable mixed modes or snapshot reconciliation.

This makes the design's stable snapshot watermark precise: it is a committed source revision, not a wall-clock timestamp or a local auto-increment value whose transactions may commit out of order. Source revisions must survive restarts and must not reset on reconnect. A new source revision epoch requires a new connection and controlled full sync, not comparing unrelated counters.

For each record, lower revisions are ignored; equal revisions with equal semantic content are idempotent; equal revisions with different content are a conflict. Compare parsed objects (including all metadata, respecting exact decimal strings), not property order. Tombstones follow the same rule. Preserve the highest accepted deletion revision. A changed event ID cannot bypass these rules.

On-demand results are fresh evidence for the current tool call only; **v1 does not write them into the synchronized projection**. A live observation may carry a newer observation time for unchanged business data without manufacturing a business revision or updating the provider. Partial/not-requested fields cannot overwrite synchronized values. Reject a live record below the locally known record revision; for an absent/deleted record reject revisions at or below its deletion/completed-snapshot floor. Also reject conflicting known business values at an equal revision. Compare only supplied known fields for this live check; an unknown/not-requested field is not evidence that the stored value changed. A query response must echo the authenticated business, return one result per requested selector in order, and identify ambiguity. A text search returns one result; `ambiguous` candidates are bounded, not an exhaustive inventory.

## Signed request authentication proposal

Use HTTPS outside explicitly configured loopback-only development. Responses use HTTPS authentication and must still pass payload/tenant validation. No redirects to arbitrary provider destinations. Provision separate 32-byte random HMAC keys per connection and direction. Do not reuse chat API keys, user tokens or keys across endpoints/providers; store secrets encrypted, show them only at issuance and support rotation/revocation. Keys themselves grant only configured scopes, never public/internal bot permissions.

Every request, including GET, requires exactly one of each header:

| Header | Value |
| --- | --- |
| `X-Fyr-Key-Id` | Opaque key ID, 1-128 ASCII letters, digits, dot, underscore or hyphen |
| `X-Fyr-Timestamp` | Unix seconds as canonical unsigned base-10 digits, at most 12 digits |
| `X-Fyr-Request-Id` | Lowercase UUID, fresh for every signed attempt |
| `X-Fyr-Signature` | `v1=` followed by 64 lowercase hex digits |

Canonical UTF-8 input to HMAC-SHA256 is these seven lines, joined by LF, with **no trailing LF**:

```text
FYR-OPDATA-V1
UPPERCASE_HTTP_METHOD
ORIGINAL_PATH_AND_QUERY
KEY_ID
UNIX_SECONDS
REQUEST_ID
LOWERCASE_SHA256_OF_ORIGINAL_BODY_BYTES
```

The request target is the exact ASCII path plus optional query sent over HTTP: preserve percent encoding and query order; exclude scheme, authority and fragment. A provider's configured path prefix is included. Do not decode/re-encode it before verification. Proxies must preserve the original request target, or the integration fails verification. GET uses an empty body and its SHA-256; reject a body on GET. POST uses `application/json`, UTF-8, no BOM, no compression. Maximum request and response body: 1 MiB. Reject malformed JSON, duplicate object keys and non-finite numbers rather than relying on a parser's last-key-wins behavior.

Verify header shape, key status/scope, body limit and constant-time signature equality, then require an absolute timestamp difference <=300 seconds and atomically reject reused `(connection, direction, requestId)`. Keep replay receipts at least until `signedTimestamp + 300 seconds` so an accepted future-dated request remains protected for its full acceptance window. Do not use signature verification as tenant authorization. Revocation is immediate, including rotation overlap ending or connection disconnection.

Retries use a new request ID/timestamp/signature but retain the same original event bytes and event ID. A captured identical signed request returns 409 `request_replayed`; a legitimate freshly signed duplicate event returns its original receipt. Retain durable event identity through connection deletion/recreation policy; do not delete receipts while an enabled connection permits replay of that event. A connection's recreation uses a new connection identity and full synchronization, not revival of old credentials.

## Push acceptance and status

One event carries one absolute upsert or tombstone. Authenticate and validate before writing an inbox receipt. `202` means that receipt was committed; projection processing is asynchronous. `Location` points to its connection-scoped receipt. Same event ID plus byte-for-byte identical body returns `200` with the original receipt's current status, even if processing later failed. Same event ID with different original bytes returns `409 event_conflict`. Senders must persist/retry the same body rather than reserialize it differently under the same event ID.

Durable workers claim/lease in a short transaction, commit, perform external I/O outside transactions, then persist the outcome. Apply projection/version checks and receipt outcome atomically. Lower version produces `ignored_older_version`; equal version/equal content produces `applied`; equal version/different content produces a failed receipt with `record_version_conflict`. After acknowledgement, asynchronous errors appear on the receipt rather than pretending the original HTTP request failed.

## Scheduled Pull, snapshots and cursors

Without a cursor, the provider starts a stable snapshot containing active and inactive offers at one committed revision watermark. All pages share the snapshot ID/watermark and cannot include a record newer than that watermark. Initial cursor state is credential/business-bound. The final snapshot page has `complete=true` and a non-empty incremental resume cursor. An empty final page is valid. Partial pages have `complete=false` and a continuation cursor. An incremental page also always returns a durable next cursor, including when there are no changes.

Advance the stored cursor only in the same transaction that applies its page. Retrying a page is safe. Keep serving existing still-fresh data during resynchronization; partial snapshot absence never deletes anything. Only on full snapshot completion may absent records at or below the watermark be removed. Preserve any concurrent Push/Pull record newer than that watermark. Record a completed-snapshot floor so an absent/deleted record cannot be resurrected later by a delayed event at or below the watermark.

The provider guarantees that the final cursor starts after the snapshot watermark without a gap. Incremental pages carry upserts/tombstones in committed revision order; complete marks the currently exhausted batch, not the end of the stream. Tombstones remain available for the supported cursor lifetime; if changes were compacted, return `410 cursor_expired`, never silently omit deletion history. Cursors cannot transfer between businesses, connections, scopes or snapshot generations. Treat them as opaque secrets and redact them from logs.

## Errors, limits and retries

| HTTP/code | Meaning and consumer action |
| --- | --- |
| 400 `validation` | Invalid shape, version or semantic constraints; repair the request |
| 401 `invalid_credentials` | Missing/invalid/stale signature, expired/revoked credential; do not blindly retry |
| 403 `insufficient_scope` | Authenticated but missing scope or business mismatch; do not retry |
| 404 `not_found` | Connection/receipt absent or not visible; do not retry |
| 409 `request_replayed` | Reused signed request ID; retry only with a new signed attempt |
| 409 `event_conflict` / `record_version_conflict` | Integrity conflict; do not auto-retry or classify as quota exhaustion |
| 410 `cursor_expired` | Discard the cursor and start controlled full resynchronization |
| 413 `payload_too_large` | Body exceeds 1 MiB; reduce payload/page size |
| 415 `unsupported_media_type` | Unsupported encoding/content type; fix request |
| 429 `rate_limited` | Retryable, honor bounded Retry-After |
| 503 `source_unavailable` | Retryable service/storage/provider failure |

Return `application/problem+json` with the corresponding status and stable code. Unexpected failures must not expose source bodies, credentials or tenant data. A temporary failure for one selector may instead use an `unavailable` result in a successful bounded query response. Unknown data and a missing record are different outcomes.

Proposed background retry policy: five attempts total with delays 10/30/120/300 seconds after attempts 1-4. For 429/503, a valid integer `Retry-After` from 1 to 3600 seconds raises the next delay if greater; invalid values are ignored. Persist attempt/due state and classify permanent errors separately. Retrying Push is safe only with its original event identity/body. Read retries do not mutate the provider; never advance a failed sync cursor. Caller-facing on-demand queries use a bounded request deadline rather than waiting through this background schedule. Admission/rate-limit numbers are per-connection operational settings, not commercial plan feature gates.

## Review gates and compatibility

Before runtime PRs, review the HMAC profile, source-wide revision guarantee, exact decimal range, public/internal field policy and provider conformance obligations. Configure freshness, request deadlines, sync intervals, rate limits and credential lifetimes explicitly at connection setup. These settings are not fixed by this format proposal.

Payload objects are closed (`additionalProperties=false`). V1 consumers must not silently accept extra fields. A payload shape change therefore needs an explicitly negotiated new major contract, not an allegedly compatible extra property. Error titles can vary; machine logic uses codes. OpenAPI's `info.version` describes this document release; body `contractVersion` and route `v1` identify the protocol. There are no payment prices, collection policies, social-channel operations or external mutation endpoints here.
