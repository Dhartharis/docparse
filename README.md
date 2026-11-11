# DocParse

DocParse has one repository with separate web client and API directories.

```text
DocParse/
  front/                         React + TypeScript web client
    src/                         Feature screens, API/session client, localization and portal entry
    public/                      Product/studio assets
    localization/                Login catalogs and portal branding snapshots
    scripts/                     Catalog validation and snapshot synchronization
    tests/                       Playwright browser tests
    package.json                 Frontend commands and dependencies
    vite.config.ts
    playwright.config.ts
    README.md
  back/                          ASP.NET Core / .NET 8 API
    Modules/                     Access, Knowledge, Bots and Chat
    ARCHITECTURE.md               Module boundaries and current backend status
    Infrastructure/              Schema migrations and shared transport/provider configuration
    AGENTS.md                    Backend working instructions
    Properties/                  Local launch profiles
    Program.cs                   Application composition
    DocParse.csproj               Backend project and dependencies
    appsettings.json
    DocParse.http
    README.md
  .gitignore                     Shared repository exclusions
  README.md                      Repository overview
```

Each side owns its source, configuration, dependencies, tests, resources and build outputs. Local backend settings remain in ignored `back/appsettings.Development.json`; `.git` stays at the repository root. Generated files live in `front/dist`, `front/node_modules`, `front/test-results`, `back/bin` and `back/obj` and remain ignored.

## Local development with Docker

Copy `.env.example` to `.env`. No paid AI key is needed: local extraction, OCR, embeddings and answers use Ollama. Keep the authorized sibling checkout `../fyr-usage` available for its local image. Initial setup:

```powershell
Copy-Item .env.example .env
docker compose up -d postgres azurite mailpit ollama
npm --prefix ../fyr-usage ci
node tools/setup-local-usage.mjs
npm --prefix front ci
npm --prefix front run build
docker compose up -d --build
docker compose exec ollama ollama pull qwen3-embedding:0.6b
docker compose exec ollama ollama pull qwen3:4b-instruct
```

Open `http://127.0.0.1:5173/`. The API and Swagger UI are available at `http://127.0.0.1:5128` and `http://127.0.0.1:5128/swagger`; Azurite listens on `127.0.0.1:10000`. PostgreSQL is available to desktop database tools at `127.0.0.1:15432` (configurable through POSTGRES_PORT; port 5432 was occupied and Windows reserved the earlier port 55432 after a restart). The local email inbox is `http://127.0.0.1:8025`. PostgreSQL, Azurite and authentication keys persist in Docker volumes. `docker compose down` stops the services without deleting that data.

The Compose configuration is local only. It does not provision Azure resources or publish images. Azure frontend publication is a separate manual GitHub Actions workflow.

See [frontend setup and integration notes](front/README.md) and [backend setup and API endpoints](back/README.md).

## Validation

```powershell
dotnet restore back/DocParse.csproj
dotnet build back/DocParse.csproj --no-restore
npm --prefix front run build
npm --prefix front run lint
npx --prefix front playwright install chromium
npm --prefix front test
git diff --check
```

Backend validation includes `python tests/check_backend_boundaries.py` and `python tests/backend_integration.py --browser`: disposable local database/API, deterministic AI double, four real browser journeys for access, documents/restoration, API/widget and owner onboarding. `python tests/onboarding_integration.py` checks invitation generations and durable mail retries. `python tests/local_ai_integration.py --metered` exercises real local Ollama/Fyr Usage and restart recovery; run `--retention` separately for deletion/purge. `python tests/usage_service_integration.py` runs the sibling service's V1/V2 suites against a temporary database. See [V1_LOCAL.md](V1_LOCAL.md) for results and [REVISION_LOCAL.md](REVISION_LOCAL.md) for the manual review route.

Owners register a new business without invitation and verify email through Mailpit. Invites grant access to existing organizations/assistants. API keys, embed, 30-day trash and the Usage panel are connected. Local tokens are measured but credit tariffs are explicitly simulation, not commercial prices; there are no live payments. Optional Stripe test billing supplies Checkout, a customer portal and durable subscription-status reconciliation, disabled by default. Test subscriptions do not change credits. See [Stripe test setup and remaining commercial work](docs/stripe-test-billing.md). Metering currently supports Ollama and blocks paid adapters. Local catalogs are bundled, with remote localization disabled in Compose. `.local/usage-private.pem` is an ignored local machine credential; back it up privately with the persisted database/Blob/key volumes. Do not delete volumes to restart services.

## Hosting paths

Application domain: `https://docparse.fyr-studio.com/` (domain setup confirmed by the user).

The directory reorganization does not change the domain or publish either application. Configure each deployment's source directory explicitly:

- Web client: project/app directory `front`, build command `npm run build`, output directory `dist` (repository-relative output `front/dist`). For Azure Static Web Apps, use app location `/front`, no API location and output location `dist`.
- API: project/service directory `back`, project `DocParse.csproj` (repository-relative project `back/DocParse.csproj`). The Azure target is the container built from `back/Dockerfile`; Compose remains local.
- Translation publication source: `front/localization/docparse`.

The frontend workflow deploys only when manually dispatched on `main`. Docker Compose and the backend Dockerfile build locally without publishing images or provisioning Azure resources.
