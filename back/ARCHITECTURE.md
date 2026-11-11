# DocParse backend architecture

Product source: [Drive product definition](https://docs.google.com/document/d/1ao033DtGdlLEpfZSGLFziUoYwa5ztnBSh7mcjROCITE/edit). Updated November 9, 2026. Local rates are simulation data.

## Deployment and boundaries

One .NET 8 deployment organized by functionality. PostgreSQL + pgvector owns structured data, metadata and vectors; private Blob Storage owns files. React/TypeScript advances vertically with database/backend changes. Docker Compose supplies PostgreSQL, Azurite, Mailpit, Ollama, API, frontend and the existing shared Fyr Usage service with a separate database. Azure is the hosting direction; this setup does not provision or deploy.

| Module | Owns | Cross-module contracts |
| --- | --- | --- |
| Access | Identity, organizations, memberships, invitations and mail outbox | IOrganizationAccess, IOrganizationInvitations |
| Knowledge | Bases, documents, vectors, ingestion, originals and retention | IKnowledgeCatalog |
| Bots | Configuration, branding, base links, grants, pending access, API keys and embed settings | IBotCatalog, IBotChannelAccess |
| Chat | Shared retrieval/answer orchestration and provider boundary | HTTP channels |
| Usage | Fyr Usage adapter, reservation lifecycle, accounting outbox, plan selection and cost scenarios | IUsageMeter |
| Integrations | Organization/source configuration, optimistic revisions and protected Push/Pull credentials | No provider/data/tool contract exposed yet |

Each module owns its DbContext/schema. Cross-module imports use only Contracts. Application/domain code does not depend on EF, Azure SDKs, upload HTTP or vector types; tests/check_backend_boundaries.py guards this. One project supplies composition; namespaces/checks provide boundaries, not separate assemblies.

Composite organization foreign keys intentionally guard tenant relationships. Extracting a service requires replacing constraints and adapting consistency, not merely moving folders. Shared storage transport and SQL migration runner belong to composition. Embedded immutable SQL migrations record checksums under an advisory lock; SQL is the authoritative history.

## Identity and permissions

Public owner onboarding atomically creates user, new organization, Admin membership and confirmation outbox. The owner chooses credentials and must verify email before login. No invitation or caller-selected existing organization/role is accepted. Verified accounts may create separate businesses through an authenticated request-ID-based operation.

Joining an existing organization requires an Admin invitation for the recipient email. Registration creates credentials/membership and consumes the invitation atomically; existing verified accounts accept with matching email. Passwords require 12 characters and complexity. Development bootstrap is an explicit CLI fixture, never a public/automatic seed.

ASP.NET Core Identity issues opaque bearer access/refresh tokens, not JWT/OIDC. Cookies are rejected. Access lasts 15 minutes, refresh seven days. Authorization checks security stamps and current memberships on each request. Password changes/logout-all revoke sessions; ordinary logout discards client tokens. Persisted data-protection keys survive restarts. Stable user IDs support future (provider, issuer, subject) links. Entra integration/password migration is not implemented; never auto-link by email.

Membership changes serialize and cannot remove/demote the last Admin. Admins manage their organization; Viewers use only assigned assistants and chat, never document/admin APIs. Membership removal cascades grants; rejoining does not revive them. Pending bot grants are bound to invitation ID/generation; activation needs matching verified membership and accepted generation. Cancelling/reissuing cannot revive old grants. All-bots selection covers only currently existing bots.

Mail uses a protected durable outbox: transactional claim/lease, commit, SMTP outside transaction, then result. Timeout 10s, lease 2m, five attempts with 10/30/120/300s delays. SMTP acknowledgement is not delivery. EN/ES/VI templates ship locally. Mailpit does not deliver real mail and the adapter refuses production. Browser confirmation returns branded success/failure.

## Ingestion and retrieval

PDF/PNG/JPEG uploads accept 10 MiB. Durable staging precedes Blob upload. SHA-256 deduplicates active documents per base under a unique index; duplicates return existing ID/status. Failed documents use explicit Retry. Staged abandoned objects are cleaned durably. No global sweep of unknown historical blobs: isolated databases share Azurite, so absence in one database does not establish ownership.

Acceptance returns 202 after storing original/queued metadata. Background PostgreSQL SKIP LOCKED claims, renewable two-minute leases, fenced completion and persistent attempt/due state recover after restart. Retryable failures get three attempts with 10/30s delays; permanent failures stop. No transaction spans OCR, provider or Blob calls.

Local Ollama has cloud disabled with no paid fallback. Poppler/Tesseract provide page text/OCR (EN/ES): 200 PDF pages, 20 OCR pages, one million characters, 1024 chunks. Chunks are 1800 characters with 200 overlap. Qwen3 Embedding 0.6B uses 1024 dimensions; Qwen3 4B Instruct answers. Legacy vectors keep their profile. Ranking filters organization, authorized active bases, ready documents and active embedding profile before distance; profiles are never mixed. Changing models requires explicit reindexing. OCR is fallible.

Answers use bounded short history and strict completed JSON. Thinking metadata, malformed/truncated output never become final answers. References group document IDs with distinct pages; retrieved page counts are not total page counts or exhaustive inventory. Chat context stays in client memory with per-assistant clearing, not a durable conversation archive.

## API and widget

Keys are per assistant, conversation-only, hashed SHA-256, shown once, expiring and atomically rotatable/revocable. All channels resolve trusted scope before shared QueryService. Embed is disabled by default and needs exact allowed origins. HTTP is loopback-only. Protected sessions last 30 minutes; changing settings invalidates old versions. CSP restricts ancestors, and the parent origin is checked before session issuance. The iframe reuses ChatScreen and built frontend assets.

Persistent minute limits: 20 messages/key, 10/session, 120/external assistant, 60 embed session creations/assistant. Origins control browser embedding, not non-browser authentication. Keys stay server-side. JSON bodies max 64 KiB. Deleted assistants cannot issue/use credentials.

## Credits and economics

Fyr Usage V2 is the sole credit authority, authenticated ES256 M2M. It receives opaque organization/resource IDs and metrics, never prompts/document content. Immutable cycle/rate snapshots and integer microcredits/nanoUSD avoid floating-point drift. Reservations precede AI; actual Ollama token counts settle idempotently through a durable outbox. Restart recovery does not replay AI. Failures track provider cost without consuming customer allowance; missing quota/service prevents AI admission.

Local rates/plans are simulation, not GPU economics or approved selling prices. Metering supports Ollama; paid adapters fail closed while metering is enabled until their measurements are implemented/validated. Deterministic legacy provider tests disable metering explicitly. Storage/egress/retrieval/processing values in the calculator are manual forecasts, not automatic infrastructure meters. Next-plan selection never resets the current cycle. Fyr Usage V1 contracts remain unchanged.

## Operational connection configuration

Integrations is a feature module inside the same deployment, with application validation/store ports and its own schema. Admin-only routes bind every operation to the current organization. Connection/credential composite tenant foreign keys, locked revision checks, a request-ID creation guard and one-current-key indexes protect integrity. Active secrets are protected with the existing persisted Data Protection key ring and scoped purposes; protection/key-ring I/O happens before database transactions. Only new receiving secrets are returned once, never through metadata reads. Replacing/disconnecting erases revoked ciphertext while retaining key metadata.

Configured Push/on-demand/scheduled modes describe the intended integration; no receiver, provider HTTP calls, scheduler, operational projection or bot grants exist in this increment. Pull base URLs require approved origins and strict URI validation, with no embedded credentials/query/fragment. Request-time destination/DNS/redirect protections are still required when Pull is implemented. The source business/destination/modes are immutable; settings and keys use revision checks. Disconnection cannot be undone by an old create replay.

See [operational connection setup](../docs/operational-connections.md) for local configuration, validation, version/merge considerations and remaining work. No new package, payment change, external business mutation or deployment is introduced.

## Retention

Bots/bases/documents have 30-day restorable trash excluded immediately from retrieval/access. Deleting bot conserves shared bases/links, revokes keys and disables embed; restoring does not reactivate channels. Bases need no remaining bot links, including trash bots, before deletion. Document deletion invalidates leases; restoration can safely requeue processing work. After deadline, module-owned workers purge metadata/vectors and durable Blob tasks. File I/O stays outside transactions. Superseded/staged logos use owned cleanup; unknown objects are preserved.

## Validation and production limits

See ../V1_LOCAL.md for commands and observed results. Temporary local databases, deterministic AI doubles and separate real Ollama/Fyr Usage scenarios cover the critical paths. Browser journeys exercise real API/database/mail with desktop/mobile accessibility checks.

Before Azure: production email, HTTPS/origins/proxy trust, secrets/least-privilege roles, shared encrypted keys, backups, observability, deployment migrations, commercial parameters and cloud costs/quotas. Entra remains future work; WhatsApp is V2. Review EPPlus licensing before commercial Excel export. Local tests do not prove production networking, email delivery, identity or free-tier eligibility.

