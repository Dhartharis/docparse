# Scheduled operational synchronization

This increment depends on on-demand Pull PR #11 and its Push/connection predecessors. It adds automatic read-only import from a provider's standard v1 changes API, durable page/cursor processing and organization Admin status/full-restart controls. It does not grant a bot, Viewer or public channel access to these records, activate payments or mutate the provider's business.

## Enable and configure

Apply additive migration `021_scheduled_sync.sql` before starting this API build. Preserve the shared Data Protection key ring and database across replacements. A connection must enable scheduled Pull, map its source business, have an approved provider origin and contain a valid provider-issued query credential. The configured interval is 60–86400 seconds.

`Integrations:ScheduledSyncEnabled` defaults to true; local Compose exposes `INTEGRATION_SCHEDULED_SYNC_ENABLED`. Setting it false pauses this process's worker and reports `paused` in the Admin status panel. Pause all API replicas consistently to pause the service. Existing data, cursors and leases remain durable. Re-enabling recovers eligible/expired work. The isolated connection-management test disables this worker so its fake origins never receive provider traffic.

The worker creates one durable job for each active scheduled connection. A missing key stops without a provider call; saving/replacing its key automatically wakes it and starts a new snapshot generation. A settings revision invalidates in-flight work while retaining the cursor if the key identity is unchanged. Replacing credentials clears the old encrypted cursor because provider cursors are credential bound. Disconnection prevents new claims and commits.

## Provider and page contract

The external source implements:

```text
GET {baseUrl}/integrations/v1/operational-data/changes?sourceBusinessId=...&pageSize=100[&cursor=...]
```

The existing base-path prefix is retained. Requests have an empty body, fresh signed request ID/timestamp and the standard HMAC profile over the exact escaped path/query. Source identity and opaque cursor are encoded as query values, never concatenated as untrusted URL syntax. The same protected outbound boundary as live Pull enforces approved HTTPS origins, pinned public DNS addresses, TLS validation, no redirects/proxies/cookies/compression and 1 MiB response/16 KiB header limits. Explicit Development loopback/host.docker.internal origins are only for local fixtures. The provider must enforce its own credential scopes, business mapping and replay protection.

The first request omits the cursor and requires a snapshot. All its pages must retain snapshot ID and committed revision watermark, contain only absolute offers at/below that watermark, and return a non-empty next cursor. Subsequent pages are read using that cursor, including after completion. An incremental page has null snapshot metadata, ordered versions above the snapshot watermark and no regression from prior incremental pages. Empty completed pages are valid. An unchanged cursor is allowed only for an empty completed page; a non-progressing page with data or continuation is rejected.

Strict closed JSON, duplicate-property rejection, exact decimals/38-digit versions, UTC/source-clock validation and offer/tombstone rules are shared with Push/live queries. Wrong business, metadata changes, malformed fields, oversized/encoded responses and record-version conflicts stop without advancing the page.

## Atomicity and mixed Push/Pull ordering

Jobs are claimed atomically with a durable two-minute lease and committed before external I/O. Provider requests have a ten-second deadline. Protection/decryption/key-ring operations occur outside database transactions. A page is then applied with a separate bounded local transaction (90-second cancellation bound), using the current generation, connection revision, credential and lease as fences.

Integrations owns the job/encrypted cursor/outcome. OperationalData owns records, active snapshot membership, ordering state and completed-snapshot floors. Their public projection contract accepts the caller's local transaction so page application, snapshot completion, encrypted next cursor and successful job outcome commit together. A rollback retains the previous cursor and all previous data. A worker with an obsolete generation or expired lease cannot commit. Connection/work-row locks and the projection's advisory lock use a consistent ingestion order across Push and scheduled imports.

Partial snapshots may add/update offers but never infer absence. Only completion removes records absent from the full snapshot at or below its watermark; concurrent Push records above the watermark survive. A durable floor covers removed/absent records so delayed Push or live evidence at/below it cannot resurrect them. Explicit higher tombstones and record revisions also retain their ordering protection. A full resync cannot move behind the prior committed snapshot/incremental position. Price/stock freshness still uses source observation/expiry and independent connection maximum ages, never synchronization time.

The cursor is protected under a purpose bound to organization, connection, generation and credential identity. It is never returned by management endpoints or included in worker error logs. Configure proxy/APM/HTTP diagnostic redaction too: a provider GET necessarily carries its cursor in the encrypted-in-transit request target. Completed floors, durable jobs and the key ring must be included in backups; no cursor/inbox/tombstone retention sweeper is introduced.

## Scheduling and failures

Incomplete pages wait two seconds before the next claim; completed batches wait the configured connection interval. This limits one source to at most 30 page admissions/minute before additional network/processing time. A single worker lane per API process is the initial implementation; multiple replicas coordinate through durable leases and skip-locked claims. Production throughput/fairness still needs measurement.

Retryable network/timeouts/transient PostgreSQL errors and provider 429/5xx have five total attempts for the current page, with contractual 10/30/120/300-second delays. A valid integer Retry-After of 1–3600 seconds raises the delay if greater. Attempts/due times survive restart. Permanent authorization, scope, payload/protocol and integrity failures stop immediately. An expired final lease terminates rather than leaving a permanently processing job.

A 410 with an existing cursor starts a new generation after the connection interval, clearing that cursor while retaining existing records and floors. A 410 on the initial cursorless request is a permanent protocol error. Explicit Admin **Restart full import** also starts a new generation, discards the old in-flight result/cursor and preserves current records until the new snapshot completes. It requires confirmation and the current connection revision; it never writes to the external source.

## Administration and validation

Admin-only routes under `/api/organizations/{organizationId}/connections/{connectionId}`:

- `GET /sync`: waiting/processing/failed/paused, attempts, next time, last successful page/completed batch, snapshot completion and a safe error code. No provider body, key or cursor is exposed.
- `POST /sync/restart` with `{ "revision": "current-connection-revision" }`: request a full restart. Stale/disabled settings conflict; cross-tenant/Viewer access is denied by the shared management boundary.

The panel refreshes while open, cancels its reads on close and distinguishes partial snapshot progress from completed import. EN/ES/VI use the separate `source_sync` namespace and catalog version 6, preserving existing assistant-save `sync` messages. All concurrent locale key sets must survive integration.

Validation commands: `dotnet build back/DocParse.csproj`, `python tests/check_backend_boundaries.py`, `dotnet run --project tests/ScheduledSyncPolicyTests/ScheduledSyncPolicyTests.csproj`, frontend install/build/lint/tests, `python tests/scheduled_sync_integration.py --browser`, `python tests/connections_integration.py` and the general `python tests/backend_integration.py` against a dedicated branch image via temporary Compose override.

The combined provider harness also runs Push/live invariants and real browser journeys. Synchronization cases cover signed GET/cursor escaping, partial/full snapshots, incremental changes/deletion, concurrent newer Push, absence floors and live denial, encrypted cursor durability, exact retry delays/Retry-After/exhaustion, cursor expiry, obsolete generations, atomic rollback on failed cursor/outcome persistence, expired final leases, permanent failures and tenant credential foreign keys. UI checks exercise all three languages, 390/1440 widths, accessibility, status and confirmation. CI runs these fixtures without production credentials and has no deploy step.

Real-provider snapshot/cursor guarantees, production proxy/DNS/TLS behavior, key storage, load, backups/restoration and operator acceptance remain release work. Bot/audience/field grants, agent tool orchestration, operational usage accounting, delegated Debt Assistant authorization and commercial payment policies remain separate features. No production deployment or PR merge is included here.
