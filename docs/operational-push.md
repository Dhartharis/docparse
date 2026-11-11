# Operational Push: prices and stock

This increment depends on connection management in PR #8. It implements the receiving half of the proposed operational-data v1 contract in PR #6; it does not implement Pull, schedules, agent tools or the Debt Assistant adapter. Both PRs remain reviewable separately. There are no deployments, payment changes, AI calls or external business mutations in this feature.

## Setup and scope

Apply additive migration `019_operational_push.sql` after `018_operational_connections.sql` before starting this API build. Migration 017 belongs to the independent Stripe PR and is intentionally not reused. Preserve the existing Data Protection key volume across API replacements and share it across replicas. Rotating/disconnecting an active connection erases its revoked credential ciphertext; it does not delete its event identities or highest record versions.

An organization Admin creates a Push connection under **Account → Data connections**, selects the source business, clock allowance and maximum price/stock ages, then gives the displayed receiving key to the source using a secure channel. The secret is displayed once. Price and stock ages are independent; source business identifiers and connection identity cannot be changed in place. No user/chat key grants access to these receiving endpoints.

Use HTTPS outside Development. Production proxy trust, original request-target preservation, shared encrypted key storage, backups, operational monitoring and real-provider acceptance remain deployment prerequisites. Local HTTP tests are loopback-only. Configuring a source does not grant any bot, public assistant or Viewer access to its data.

## Wire requests

- `POST /api/integrations/v1/connections/{connectionId}/events`: one absolute offer upsert or deletion tombstone per request.
- `GET /api/integrations/v1/connections/{connectionId}/receipts/{receiptId}`: authenticated receipt status. A receipt belonging to another connection returns 404.
- `GET /api/organizations/{organizationId}/connections/{connectionId}/records`: authenticated organization Admin preview, up to 100 non-deleted offers ordered by external ID and a total count. This is an internal management response, not the provider wire representation or a live query API.

POST requires UTF-8 `application/json`, no BOM or compression, at most 1 MiB. GET must have an empty body. Unknown and duplicate JSON properties are rejected, as are invalid Unicode, NUL text (unsupported by PostgreSQL JSONB), invalid UTC dates and values outside the contract ranges. Timestamps use UTC `Z` and at most seven fractional digits. Observations, updates, event occurrence and deletions cannot be later than the configured clock allowance. Old records may be received but do not become fresh merely by arriving now.

Every signed attempt needs exactly one of each header:

| Header | Format |
| --- | --- |
| `X-Fyr-Key-Id` | Issued identifier, ASCII letters/digits/dot/underscore/hyphen, 1–128 characters |
| `X-Fyr-Timestamp` | Canonical unsigned Unix seconds, up to 12 digits, within inclusive ±300 seconds |
| `X-Fyr-Request-Id` | Fresh lowercase UUID |
| `X-Fyr-Signature` | `v1=` followed by 64 lowercase hexadecimal digits |

Decode the 64-character signing secret into 32 bytes. HMAC-SHA256 signs seven UTF-8 lines joined by LF, without a final LF:

```text
FYR-OPDATA-V1
UPPERCASE_HTTP_METHOD
ORIGINAL_ASCII_PATH_AND_QUERY
KEY_ID
UNIX_SECONDS
REQUEST_ID
LOWERCASE_SHA256_OF_ORIGINAL_BODY_BYTES
```

Preserve percent escapes and query order; exclude host, scheme and fragment. A proxy that rewrites the request target invalidates the signature. GET signs the hash of an empty body. Source business must match the authenticated connection; neither request bodies nor receipt IDs select a tenant. Key resolution/decryption happens before the short acceptance transaction, then configuration revision, revocation and expiry are checked again under a connection lock.

Example deletion (replace IDs/times and sign the exact bytes sent):

```json
{
  "contractVersion": "1",
  "sourceBusinessId": "business-demo",
  "eventId": "aabbccdd-1122-3344-5566-778899aabbcc",
  "occurredAt": "2026-11-11T12:00:00Z",
  "change": {
    "kind": "delete",
    "tombstone": {
      "externalId": "offer-1",
      "version": "12",
      "deletedAt": "2026-11-11T12:00:00Z"
    }
  }
}
```

The full upsert includes every offer field specified by PR #6: presentation identity, product ID, nullable SKU, name, presentation, unit, version, observation metadata, active state, explicit price state and availability. Money and quantities are exact non-negative decimal **strings** (18 integer / 6 fractional digits); versions are positive integer **strings** up to 38 digits. Currency shape is three uppercase letters; the provider is responsible for ISO currency membership and unit mapping. No currency conversion or implicit tax calculation occurs. Unknown values stay unknown; available stock already excludes reserved stock.

## Durability and ordering

202 means the event and receipt are committed to `integrations.push_inbox`; it does not mean the projection has been applied. A fresh signed retry of the identical event ID and original body bytes returns 200 with the original receipt. Reusing the event ID with different bytes returns 409 `event_conflict`. Reusing a signed request ID returns 409 `request_replayed`, including after restart or key rotation. Durable nonces are retained through `signedTimestamp + 301 seconds` to cover the full inclusive timestamp-second boundary. Do not reserialize a retried event; keep the original bytes.

The worker atomically claims work with a two-minute durable lease, commits, then processes it. There is no network or file I/O in a database transaction. The public projection contract accepts a caller-owned `DbTransaction` specifically so the module-owned record update and fenced inbox outcome share one short PostgreSQL transaction. Neither module reads the other's tables. Business validation stays provider-independent; Npgsql details stay in infrastructure. An obsolete or expired lease cannot commit a projection/outcome or overwrite a newer worker's result.

Projection identity is `(organization, connection, externalId)`. Numeric versions compare without floating point. Lower versions yield `ignored_older_version`; equal version/equal parsed content is idempotent; equal version/different content yields a failed receipt with `record_version_conflict`. JSON property order is irrelevant; exact decimal strings and all metadata remain significant. Deletion tombstones retain their highest version and prevent delayed resurrection. A later higher upsert can reactivate the offer.

Transient Npgsql failures and timeouts retry after exactly 10, 30, 120 and 300 seconds, for five total processing attempts. Permanent failures stop immediately. Retry/attempt/lease state survives process restarts. An expired final lease terminates with `processing_failed`. Sender retries are separate from these worker retries.

Receipt status is `accepted`, `processing`, `applied`, `ignored_older_version` or `failed`. Only failed receipts have `record_version_conflict` or `processing_failed` as `errorCode`. Source bodies, credentials and product content are excluded from processing-error logs.

## Freshness and administration

The Admin preview uses source `observedAt`, source `validUntil` and the connection's independent maximum ages. Equality at expiry is expired. Inactive offers are never current; unknown price is never current; untracked stock is not current. Known quantities are displayed separately from freshness, without subtracting reservations again. The preview formats exact decimals without converting them to JavaScript floating-point values. Refresh fetches a new snapshot; expired display data is not evidence for a future chat response.

Disconnection rejects new signed admissions and preview reads. Already claimed work may finish into the private projection; it grants no data access. Re-creating a source gets a new connection identity, never revives the old key or event stream. There is no inbox/tombstone pruning in this increment: identities must outlive potential event replays. Retention sizing and a safe archival policy remain operational follow-up work.

The receiver currently enforces 120 admitted signed requests (events and receipt polls combined) per connection per minute in PostgreSQL, plus a 600-request/IP/minute in-memory abuse guard. These are server policy limits, not editable connection fields yet. A 429 is retryable with a fresh signed request ID; no queue acceptance is implied. Connection-specific configurable rate/deadline policies remain for the Pull/tool rollout.

## Validation

```text
dotnet build back/DocParse.csproj
python tests/check_backend_boundaries.py
dotnet run --project tests/ConnectionPolicyTests/ConnectionPolicyTests.csproj
dotnet run --project tests/PushPolicyTests/PushPolicyTests.csproj
npm --prefix front ci
npm --prefix front run build
npm --prefix front run lint
npm --prefix front test -- --workers=2
python tests/push_integration.py --browser
python tests/backend_integration.py
```

Integration scripts create and remove isolated local databases, containers and test images. Run the main regression against an image built from this branch through a temporary Compose image override; do not replace a running main image. UI tests cover English, Spanish and Vietnamese, 390/1440 widths, unknown/error states, precision and accessibility; the Push integration also exercises the real Admin browser against signed seeded events.

The new validation workflow runs on stacked PRs as well as main and contains no deploy step. Real provider/proxy conformance, production load/retention, full operational contract publication, Pull/snapshot synchronization, bot permissions, agent grounding and Debt Assistant onboarding remain outside this increment. Stripe account setup and real Stripe test-mode acceptance remain with PR #7. WhatsApp, Zalo and Facebook remain deferred.
