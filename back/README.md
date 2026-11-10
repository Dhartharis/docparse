# DocParse backend

.NET 8 modular monolith. [ARCHITECTURE.md](ARCHITECTURE.md) defines ownership and limits. Product scope: [Drive](https://docs.google.com/document/d/1ao033DtGdlLEpfZSGLFziUoYwa5ztnBSh7mcjROCITE/edit).

## Local environment

Follow the root [README](../README.md) for initial setup, including sibling Fyr Usage, machine key and frontend assets. Afterwards use `docker compose up -d` from the root.

- API/Swagger: http://127.0.0.1:5128/swagger
- Readiness (PostgreSQL/Blob): http://127.0.0.1:5128/health
- Mailpit: http://127.0.0.1:8025 (local inbox, no real delivery)
- PostgreSQL: 127.0.0.1:15432, database/user docparse; separate fyr_usage_local database
- Azurite: http://127.0.0.1:10000/devstoreaccount1
- Ollama: http://127.0.0.1:11434; Fyr Usage: http://127.0.0.1:3010

Compose runs immutable SQL migrations before API startup. Repeat safely with `docker compose run --rm migrate`. Never edit an applied migration. Data and protection/model keys persist in Docker volumes; `docker compose down` preserves them. The ignored .local/usage-private.pem signs M2M requests; do not publish it. Nothing provisions/deploys Azure.

## Identity and onboarding

Public `POST /api/onboarding/register` accepts email, userName, password, organizationName and locale: create a new business owner without invitation. It cannot join an existing organization or choose arbitrary roles. Confirm email via Mailpit before login. Browser confirmation links return branded success/failure screens.

Existing-business access remains invitation-bound: Admin chooses email/role, recipient chooses credentials or accepts with an existing matching verified account. Framework `/api/auth/register` stays blocked; it is not the owner onboarding endpoint. Normal passwords require 12 characters and complexity. Development-only `--bootstrap-local-admin` remains an explicit fixture CLI, not a public seed.

Login uses `POST /api/auth/login` with email containing username or email, plus password. Opaque bearer tokens last 15 minutes; refresh lasts seven days. Cookies are rejected. Tokens stay in browser memory. Memberships/security stamps are checked on protected requests; password reset and logout-all revoke authorization. Ordinary logout discards local tokens.

| Method | Route | Access |
| --- | --- | --- |
| GET | /api/auth/me | Current verified account |
| POST | /api/auth/logout-all | Revoke all own sessions |
| GET/POST | /api/organizations/ | Own memberships / create separate business (optional requestId) |
| GET | /api/organizations/{org}/members | Admin |
| PATCH/DELETE | /api/organizations/{org}/members/{user} | Admin; last Admin protected |
| GET/POST | /api/organizations/{org}/invitations | Admin; list pending or invite/resend email, role, locale |
| DELETE | /api/organizations/{org}/invitations/{id} | Admin; cancel pending invitation |
| POST | /api/invitations/preview | Token body; valid invitation context |
| POST | /api/invitations/register | email, userName, password, token |
| POST | /api/invitations/accept | Matching confirmed account and token |

Invitations expire after seven days. Active tokens can be resent with a 60s cooldown; expired/revoked/accepted invitations require a fresh generation. Pending bot access is generation-bound. Tokens are hashed, with temporary protected copies for mail delivery; accepted/revoked/expired copies are cleared. Mail outbox claims persist before SMTP, then retry outside transactions. SMTP acknowledgement does not prove delivery.

Identity uses stable DocParse GUIDs; future Entra identities must map by provider/issuer/subject, never email alone. Entra/password export is not implemented. Production mail and key protection need separate configuration.

## Knowledge, bots and chat

Below, root is `/api/organizations/{org}`. All administration is Admin-only. Scoped inaccessible resources return 404, anonymous requests 401.

| Method | Path below root | Purpose |
| --- | --- | --- |
| GET/POST | /knowledge-bases/ | List/create name |
| PATCH/DELETE | /knowledge-bases/{kb} | Rename / trash unlinked base |
| POST | /knowledge-bases/{kb}/documents/extract | multipart file; PDF/PNG/JPEG, max 10 MiB |
| GET | /knowledge-bases/{kb}/documents/all | Processing summaries |
| GET/DELETE | /knowledge-bases/{kb}/documents/{id} | Detail / trash |
| GET | /knowledge-bases/{kb}/documents/{id}/file | Private original |
| GET | /knowledge-bases/{kb}/documents/{id}/export/csv | Ready fields; Excel export also exists, licensing review required |
| POST | /knowledge-bases/{kb}/documents/{id}/retry | Requeue failed stored original |
| GET/POST | /bots/ | Authorized list / Admin create |
| PATCH/DELETE | /bots/{bot} | Settings / trash |
| GET/PUT | /bots/{bot}/knowledge-bases | Get/replace knowledgeBaseIds atomically |
| POST/GET | /bots/{bot}/logo | Upload PNG / authorized read |
| GET/PUT | /bots/{bot}/viewers | Explicit userIds assignments |
| GET/POST/DELETE | /bots/{bot}/access/ | Email grants/pending; POST email/allBots; DELETE email query |
| POST | /bots/{bot}/access/resend | Resend pending email invitation |
| POST | /bots/{bot}/messages | Authorized Admin/assigned Viewer conversation |

Chat body includes question and optional bounded history. Enter/clear-context are client interactions. Retrieval never exposes raw internal Blob keys; original downloads require authenticated fetch. Viewer cannot access document/admin routes. PNG logos max 1 MiB, 1024×1024; allowed accents #7441ec, #2457c5, #186a47, #a42d54.

Uploads return 202 with queued status before AI processing. Durable PostgreSQL leases/retries survive restart. Lists expose queued/processing/ready/failed; only ready documents are searched/exported. SHA-256 per-base dedup returns existing ID/status; explicit Retry handles failures. OCR/text runs locally through Poppler/Tesseract EN/ES, with page/text/chunk limits in ARCHITECTURE.md. Qwen3 local models are installed once and persist; cloud is disabled. Profiles are not mixed; changing embedding model requires explicit reindexing.

## External API and widget

Admin management root `/bots/{bot}/channels` provides GET/POST keys, DELETE keys/{id}, and GET/PUT embed. Create key body: name, expiresInDays (1–365, default 90), optional rotateKeyId. Secret appears once, only hash persists.

Technical conversation: `POST /api/v1/organizations/{org}/bots/{bot}/messages`, header X-DocParse-Key. These keys cannot administer or download files. Use only from trusted servers. Widget frame/loader: `/api/embed/{bot}/frame`, `/api/embed/{bot}/loader.js`; message transport uses protected session header X-DocParse-Session, never an API key in a URL.

Enable widget explicitly and configure exact allowed origins. Built front/dist must exist and is mounted read-only into API. Local demo: http://127.0.0.1:5173/widget-demo.html#bot=BOT_ID. Changing settings invalidates sessions. Origin restrictions control browser embedding, not non-browser access; persistent rate limits and credit caps remain necessary.

## Trash and credits

GET /bots/trash, /knowledge-bases/trash and /knowledge-bases/trash/documents list scoped trash. POST the relevant resource path plus /restore within 30 days. Deleting bot preserves shared bases/links but revokes keys/disables widget. Bases must be unlinked from all bots (including trash) first. After 30 days, owned workers purge metadata/vectors and durable Blob cleanup jobs.

Admin usage root `/usage`: GET /, /plans, /breakdown; PUT /plan with planKey for next cycle; POST /estimate for manual cost scenarios. Fyr Usage is the only credit calculator. Local token counts are real, rates/plans explicitly simulation. Provider outage or cap prevents admission before AI. Durable reporting survives API restart. Metering currently supports Ollama; paid adapters fail closed when metering is enabled. No payment collection.

## Validation

From repository root:

```powershell
dotnet build back/DocParse.csproj
python tests/check_backend_boundaries.py
python tests/backend_integration.py --browser
python tests/onboarding_integration.py
python tests/local_ai_integration.py --metered
python tests/local_ai_integration.py --retention
python tests/usage_service_integration.py
```

Run metered and retention scenarios separately. Tests create/drop isolated databases/API containers and use only local providers/doubles; no production keys. See [../V1_LOCAL.md](../V1_LOCAL.md) for executed results and production limitations. Azure setup, commercial tariffs, production email, backups, Entra and EPPlus licensing require work before publication.

