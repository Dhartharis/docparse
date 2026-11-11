# On-demand operational queries

This increment depends on Push PR #9 (and connections PR #8). It implements the read-only query portion of proposed operational-data v1, initially as an organization Admin preview under **Account → Data connections → Query source now**. Chat/tool permissions and scheduling are separate features. There is no payment, AI call or external business write in this increment.

## Setup

Apply additive migration `020_live_queries.sql` before running this API. Keep the existing shared/persisted Data Protection key ring. Create a connection with on-demand Pull enabled, a provider base URL and a source-business mapping. Approve the provider's exact origin using `Integrations:AllowedProviderOrigins` (local Compose: `INTEGRATION_PROVIDER_ORIGIN`). Enter the provider-issued key ID and 32-byte secret encoded as 64 hexadecimal characters using **Provider query key**. This credential is separate from DocParse's receiving key.

The source must implement `POST {baseUrl}/integrations/v1/operational-data/query`; a configured path prefix is retained. DocParse signs the exact serialized body and request path/query using the same seven-line HMAC profile as Push. Every attempt has a new UUID and timestamp. The provider must authenticate, scope and prevent replay according to the shared contract. This implementation does not create or change provider catalog data.

## Consumer endpoint and behavior

`POST /api/organizations/{organizationId}/connections/{connectionId}/query` requires organization Admin access. Viewers and other tenants receive no source information. The internal request is:

```json
{"fields":["catalog","price","availability"],"selectorKind":"text","values":["Milk"]}
```

Selectors are `text` (one non-empty value, up to 256 Unicode scalars), `externalIds` or `skus` (up to 50 unique values, up to 128 scalars each). Fields are a unique non-empty subset of `catalog`, `price`, `availability`. The wire adapter converts this to v1's `selector` object and adds the trusted source business and contract version; the caller cannot select an arbitrary destination or business.

The provider response must echo the authenticated source business, contract version, selector values and order. Found results contain one offer; ambiguity contains 2–10 distinct offers; not-found/unavailable contain none. Every offer uses the same strict closed JSON/UTC/decimal/version validation as Push. Unrequested price and availability must explicitly be unknown with `not_requested` reasons. Identifiers returned for ID/SKU selectors must match those selectors. Malformed, oversized, compressed, mismatched or inconsistent responses fail as a whole, without exposing provider bodies.

The management response includes query time, selector outcomes and offer records with independent price/availability freshness flags. Amounts and quantities retain exact strings; unknown is never zero. The Admin UI shows ambiguous and missing results explicitly, and displays source observation times and expiry state. A live query does **not** write its offers into the synchronized projection or silently fall back to stored data. There is no automatic retry or paid entitlement/credit change for this Admin diagnostic preview. Tool usage accounting and user-facing answer behavior remain separate work.

Live evidence is compared with the known local record revision/tombstone. Lower versions and equal-to-deletion versions are rejected. At equal version, conflicting known business values are rejected; observation time may refresh without manufacturing a new business revision. Unknown/unrequested values cannot overwrite or contradict stored values. Exact decimal strings remain significant, consistent with v1. Higher live versions may be returned but are not persisted, so an existing tombstone remains intact.

## Outbound protections and limits

- Every query revalidates its configured origin and credentials. Admission uses a short locked transaction to fence configuration/key changes and enforce 30 attempts/connection/minute in durable PostgreSQL windows. Failed provider calls consume admission capacity. The result is fenced again after the external call; credential replacement or disconnection invalidates it.
- The five-second deadline covers credential resolution, DNS/network/body reading, validation and result checks. No transaction spans the provider call. On-demand requests do not enter the background retry schedule.
- Production origins require HTTPS. Automatic redirects, proxy inheritance, cookies, decompression and HTTP connection coalescing are disabled. Certificates retain normal hostname/chain validation.
- The socket callback resolves DNS, rejects the entire answer if any address is non-public/special-purpose, then connects directly to the validated addresses. There is no second hostname lookup between validation and dialing. Private, loopback, link-local, metadata, multicast, transition and conservative special-purpose ranges fail closed. Explicit Development loopback/`host.docker.internal` origins are the only local test exception; do not deploy Development configuration publicly. Some legitimate special-purpose public addresses are deliberately excluded.
- Responses are capped at 1 MiB, headers at 16 KiB, and require uncompressed UTF-8 JSON. A five-second bound also prevents an unbounded streaming body. Source response text/credentials are not returned in errors.

The server limits are explicit initial operational policies, not commercial plan gates or editable per-connection limits yet. Provider 429/503 `Retry-After` integer values from 1–3600 seconds are forwarded when valid; no client retry loop ignores those values. Provider 401/403 are translated to source-specific 409 errors, so rejected provider credentials do not expire the user's DocParse session.

## Validation and release

Executed commands include `dotnet build back/DocParse.csproj`, `python tests/check_backend_boundaries.py`, `dotnet run --project tests/LiveQueryPolicyTests/LiveQueryPolicyTests.csproj`, frontend install/build/lint/tests, `python tests/live_queries_integration.py --browser`, and the general `python tests/backend_integration.py` regression against an isolated image via a temporary Compose override.

The provider double verifies actual HMAC headers/body/prefixes and exercises selectors/fields, unknown/stale evidence, ambiguity, strict malformed/oversized/encoded responses, credential/scope/rate/outage errors, timeout, revision/deletion checks and in-flight revocation. Isolated PostgreSQL fixtures prove read-only results and tenant admission. The harness also runs the existing Push invariants and six lease-fencing checks. Policy tests exercise a real socket callback against a blocked loopback listener without connecting to it. Browser checks cover EN/ES/VI, desktop/mobile, accessibility, source errors without logout and the real API-to-local-provider query journey.

CI validates stacked PRs and runs the combined local-provider suite without production credentials. No deployment is performed. Before use with a real provider, complete provider/proxy/DNS/TLS conformance and operational load validation. Before exposing queries to conversations, implement explicit bot/audience/field grants, bounded tool orchestration and usage accounting. Shared Debt Assistant authorization and commercial payment policies remain unimplemented here. Preserve all translation keys and advance beyond version 5 when merging concurrent locale branches.

The pinned socket approach uses the platform's [SocketsHttpHandler.ConnectCallback](https://learn.microsoft.com/dotnet/api/system.net.http.socketshttphandler.connectcallback); the existing shared operational contract remains the wire-format reference.
