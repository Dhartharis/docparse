# DocParse frontend

React + TypeScript + Vite client. Features are delivered vertically across database, API and screens, with local browser review before expanding the next feature.

## Run locally

The recommended complete local environment is Docker Compose from the repository root. Complete the [initial setup](../README.md#local-development-with-docker) first, including the sibling Fyr Usage checkout, local signing key, frontend build and Ollama models. For subsequent starts, use `docker compose up -d`. This runs the frontend, API, PostgreSQL/pgvector, Azurite, Mailpit, Ollama and Fyr Usage without publishing to Azure.

For frontend-only work, a Node version supported by Vite (developed with Node 24) is required:

```powershell
cd C:\Proyectos\DocParse\front
npm ci
npm run dev -- --host 127.0.0.1
```

## Validation

```powershell
npm run build
npm run lint
npx playwright install chromium
npm test
```

Browser tests cover required-field validation, keyboard interaction, password visibility, real login requests and credential-storage isolation, language selection/fallback, remote catalogs, offline cache, malformed packs, responsive overflow, and automated accessibility scans in English/Spanish/Vietnamese at 320, 390, 768, and 1440 pixels. Automated tests intercept localization requests and never depend on production services. Automated scans are not a full accessibility certification.

## Scope and source of truth

- Product: [DocParse — Product Definition & Pricing](https://docs.google.com/document/d/1ao033DtGdlLEpfZSGLFziUoYwa5ztnBSh7mcjROCITE/edit).
- React follows the product definition. Vite provides a small client-side build with no SSR or routing infrastructure yet.
- Visual reference: `C:\Proyectos\fyr-portal\DESIGN.md` and its current template. Product and studio SVG assets were copied unchanged from that repository; gold remains the product mark, violet the interaction accent.
- Target application URL: `https://docparse.fyr-studio.com/`, per the current instruction. The product document and portal now use this domain.
- English is the complete bundled fallback; Spanish and Vietnamese are also bundled. Only the explicit language preference and validated public translation packs are persisted. Credentials are submitted only to the local authentication API and are never persisted in browser storage. Access/refresh tokens stay in memory; reloading or closing the page requires signing in again. Refresh runs automatically while the page remains open.
- Public owner registration creates a new business without an invitation and requires email confirmation before login. Joining an existing business requires an Admin invitation; the recipient chooses credentials or accepts with an existing verified account. Confirmation resend, username/email login, password recovery/reset and organization creation use real backend endpoints. Account and organization roles come from the server. Viewers see only assigned bots; chat renders answers and source filenames. Ordinary logout clears the current browser session; logout everywhere revokes all sessions through the API.

## Feature structure and local review

- `src/screens/auth`: login, registration and recovery, preserving the original visual design.
- `src/screens/workspace`: trusted account/membership loading, organization onboarding and account actions.
- `src/screens/bots`: create/edit bots, link Knowledge Bases and grant/revoke Viewer access and save private logos/accent colors, with shared synchronization feedback and server-validated organization scope.
- `src/screens/knowledge`: Admin Knowledge Base creation/selection, document uploads, list/detail, authenticated original/CSV downloads and recovery states.
- `src/services/knowledge.ts`: typed organization-scoped Knowledge HTTP adapter.
- `src/screens/chat`: authorized bot selection, questions, answers, sources and retry/empty states.
- `src/services/api.ts`: local API transport, in-memory session and refresh coordination.

Docker enables Vite file polling so Windows bind-mounted changes invalidate cached modules. Vite proxies `/api` to the local API (`VITE_API_PROXY_TARGET`, default `http://127.0.0.1:5128`; Compose uses `http://api:8080`). No secret belongs in a `VITE_*` variable. Sign in at `http://127.0.0.1:5173`. An Admin opens Account → Manage team and invites an email; the recipient follows the Mailpit link at `http://127.0.0.1:8025`, selects their username/password and confirms their email before signing in. Owners can also create a new business through public registration without an invitation; existing businesses retain invitation-bound access. The first local Admin uses the explicit development bootstrap described in the backend README. Confirmation links use the API's configured `PublicApi:BaseUrl`, so Docker-internal hostnames do not leak into browser links.

The Admin workspace now uses vertical assistant cards, a left create card, and four internal tabs: parameters, knowledge, access and API. The header shows username and role, with organization below. Email search/add/remove lives in Access; the modal checkbox grants only bots existing now. Nonmember emails queue an invitation and remain pending until verified acceptance activates grants for that invitation generation. Knowledge reuses complete bases by reference without copying files. Upload supports drag/drop or device selection, at most ten files per batch, processed sequentially. Parameters and the integrated logo autosave after a short pause. Requests are serialized; the sync indicator confirms server acknowledgement. Pending changes prevent internal navigation/sign-out; errors retain the draft with retry/discard recovery. Knowledge links persist immediately; rename uses a pencil with debounce/Enter, and checkboxes only select bases for confirmed batch unlink. The API tab manages expiring chat-only keys and an opt-in origin-restricted widget.

Knowledge administration is connected to PostgreSQL-backed endpoints and local Blob downloads. Admin bot creation/editing, Knowledge Base links and Viewer assignment/revocation are connected. Bot parameters/logo autosync, while knowledge links and email access use their own operations; each association replacement is atomic on the server. Owner onboarding, invitation preview/acceptance, branded confirmation and durable local mail are connected; production delivery still requires an adapter. Team management now supports confirmed role changes/removal, last-Admin errors and reloading the current account’s trusted memberships after changes. Bot branding supports a persisted accent from four accessible presets and an authenticated private PNG logo. Knowledge Bases can be renamed without replacing their IDs or documents. A new Admin currently sees the honest empty-bot state. Chat history is held in page memory; durable conversations and persistent production browser sessions remain work.

`python tests/backend_integration.py --browser` from the repository root additionally exercises Admin invitation, recipient registration, Mailpit confirmation, username/email login, membership persistence, Viewer isolation and password recovery against an isolated real PostgreSQL/API instance. It also runs Knowledge Base creation, document upload with the deterministic local AI double, persistence after sign-out/sign-in, exact original-file downloads from Azurite and formula-safe CSV exports, bot creation/editing, knowledge connections, Viewer grants, chat with sources and subsequent access revocation. Narrow 320px, tablet 768px and desktop 1440px layouts are scanned and visually reviewed. It requires the local Compose services and frontend dependencies. The normal frontend suite uses controlled API responses, including assigned chat, revoked access, offline handling, accessibility and responsive layouts; the real-service test is skipped outside that runner. Neither suite calls paid AI services or Azure.

The Docker-local API now uses Ollama models and requires no cloud key. Base creation, browsing and existing downloads require no AI call. Local extraction reads PDF pages or OCRs images/scanned pages; embeddings and answers run in Ollama. A paid provider requires its metering adapter to be implemented and validated before enabling it with accounting. No key was added and no paid call was made during this feature. The UI reports processing unavailability instead of a generic server error. Automated tests use a local provider double and dummy key, never a live provider.

Docker-local uploads accept PDF/PNG/JPEG originals up to 10 MiB and return HTTP 202 after durable acceptance. Background workers extract page text/OCR and embeddings; the UI polls queued/processing/ready/failed states while users continue editing. SHA-256 deduplicates active documents within a base; failed processing supports explicit Retry using the stored original. A timeout/uncertain acceptance requires checking the list before manual re-upload. Account → Trash supports 30-day restoration, with durable cleanup after expiration. Only ready documents can be searched or exported. CSV and original downloads are connected; Excel export stays out of the UI until the EPPlus licensing decision is resolved.

`npm run build` writes static files to `front/dist`. Production API routing, HTTPS/origins and identity remain separate deployment work. Builds do not publish anything.

## Fyr Localization integration

Public endpoint: `https://fyr-localization-production.up.railway.app/v1/products`.

- Login strings use `docparse/locales` and `docparse/locales/{locale}`. The authoring files under `localization/docparse` follow the existing service publishing contract. EN/ES/VI version 1 was published and read back on November 3, 2026. The new feature strings are bundled as version 2 and have not been published; remote version 1 cannot replace them.
- Branding reads the existing `fyr-portal` catalog: `doc.description` is the complete shared slogan, `doc.category` the category, and `footer.copyright` the studio copyright. These strings are not authored again in the DocParse catalog.
- `localization/fyr-portal` contains generated offline snapshots of those three portal-owned keys. Refresh them with `npm run sync:portal-brand`; do not edit them as an independent slogan. Builds validate all included locales without network access using `npm run check:locales`.
- Initial render uses validated cache or bundled text. The client requests manifests and packs on entry, language changes, visibility restoration and every five minutes. It validates locale, version, required keys and interpolation placeholders before applying or caching data. Network/invalid-data failures preserve the current text; older advertised versions cannot replace newer cached packs.
- Requests omit credentials. No Supabase credentials or provider SDK are included in the frontend. Changes to branding and login text update independently; a failed login catalog cannot prevent the portal slogan loading.
- The existing portal passes `?lang=...`; DocParse honors that choice on entry and updates it if the user changes language. Otherwise a saved explicit choice precedes browser preference, with English fallback.
- The Vite development server proxies `/localization-api` to the public service, allowing local preview without opening production CORS to localhost. Production builds call the public service directly.

### Remaining production wiring

The service's `src/browser-cors.ts` and `test/browser-cors.test.ts` in `C:\Proyectos\fyr-localization` now cover GET locale resources for `docparse` and `fyr-portal` from `https://docparse.fyr-studio.com`. Deploy that server change. If Railway already sets `BROWSER_ALLOWED_ORIGINS`, add the DocParse origin to that configured list as well; the environment setting overrides defaults. Live API readback currently returns 200 for the published catalog but no CORS header for the DocParse origin, so direct production browser reads remain blocked until this is deployed. Bundled/cached text remains available.

The existing publisher CLI's optional country stage currently throws `ENOENT` for a locale-only source without `countries.json`. This delivery used its exported `publishLocalization` function directly (dry run, then publication and readback), leaving country resources untouched. The unrelated CLI issue has not been modified.

## Portal entry transition

The entry completes the design discussed in the chat **Evaluar referencia para la página principal**: the portal expands yellow triangular particles to a full yellow frame; DocParse gathers that yellow into its original mark on a black canvas, with login on the right. The portal already sends `?entry=fyr-portal&lang=en|es|vi`.

Preview: `http://127.0.0.1:5173/?entry=fyr-portal&lang=es`.

- `index.html` paints the same `#ffdb38` cover before the React bundle loads, only for a fresh portal arrival. Direct visits, reloads, history restoration and reduced motion skip it. A four-second CSS/JS failsafe removes the initial cover.
- `src/portal-entry.ts` samples the existing DocParse SVG and gathers 2,600 triangular particles into its exact contours over 1.4 seconds, then hands off to the original animated SVG. On narrow layouts it targets the visible header mark, since the large illustration is hidden there.
- `src/PortalEntry.tsx` owns the effect lifecycle. It cleans up correctly under React Strict Mode. The canvas is decorative, does not capture pointers or focus, and disappears immediately on keyboard/pointer interaction, resize, hidden-page navigation, reduced-motion changes, or image failure. A 2.4-second animation safety timeout prevents a stuck overlay.
- The `entry` parameter is consumed with `history.replaceState`, retaining `lang`, other query parameters, the fragment and history state. Reloading the resulting URL does not replay the entrance.
- Successful login reuses this transition into the workspace header logo, with the same reduced-motion and cancellation behavior. Portal files, deployments and dependencies are unchanged.

Browser tests use controlled animation time and cover initial color, completion/cleanup, reload behavior, language preservation, direct/reduced-motion visits, mobile target selection, resize/keyboard cancellation and image-load failure. Desktop intermediate frames and the completed screen were also visually inspected.

## GitHub deployment

Workflow: `../.github/workflows/deploy-front.yml`. Pull requests targeting `main` and pushes to `main` affecting `front/` or the workflow run validation only. Azure deployment happens only when the workflow is manually dispatched on `main`.

The workflow uses Node 24, `npm ci`, lint, build/catalog validation and Playwright Chromium tests. It uploads only `front/dist` to the existing Azure Static Web App `fyr-docparse`, with both automatic app build and API build disabled in the Azure upload action. No backend is built or deployed.

GitHub repository: `Dhartharis/docparse`. Required Actions repository secret: `AZURE_STATIC_WEB_APPS_API_TOKEN_DOCPARSE`, containing the deployment token of `fyr-docparse` (not the portal's token). Keep that credential in encrypted GitHub Actions secrets, never in source, frontend environment variables or build output.

`public/staticwebapp.config.json` is copied by Vite to the output root. It enables SPA navigation fallback, excludes asset/API/development-proxy paths from fallback, and asks clients to revalidate `index.html` so hashed bundles can update. The production domain is `https://docparse.fyr-studio.com/` and the Azure host is `https://lemon-dune-02b120610.4.azurestaticapps.net`.

The deployment workflow includes only `front/`. Backend publication remains separate and manual.

The parameter sync indicator now lives in the assistant card header, opposite the logo, using a matching 58px icon container and a visible status label. The redundant workspace title/team row is removed; the organization picker remains conditional on multiple memberships. Bot email permissions remain in Access, while organization role management is available under Account → Manage team.

Authenticated layouts fill the viewport using `100dvh`; header, footer and card chrome stay within the available height. The assistant deck uses one horizontal row with measured column capacity, previous/next controls and touch scrolling. Phones show one column at a time, including short landscape phone layouts. Long card content scrolls inside its keyboard-focusable panel; chat uses an internally scrolling conversation. Desktop styles compact the parameters at common laptop heights. The login layout retains its existing document flow for form accessibility.

Knowledge uses the same header sync indicator as parameters and access operations. Base names expand documents; independent checkboxes support confirmed batch unlink from the current assistant only. Links/creation persist immediately, with pencil autosave renaming. File picking/drop starts uploads with per-file acknowledgement. Content deduplication prevents duplicate active documents; explicit Retry handles failed processing. Account → Trash provides 30-day restoration; shared bases require unlinking before deletion. Module-owned workers clean expired/staged files.

The remove-selected action sits beside New knowledge base and is disabled until at least one base checkbox is selected. Missing/unavailable processing errors no longer imply that a pre-write 503 may have saved a document. Local chat references can include page numbers. The main Docker stack uses real local Ollama models; cloud keys remain absent.

Document acceptance is now asynchronous (202): upload transfers the original, then queued/processing/ready/failed states are polled in the document list. Processing never changes the header configuration indicator or locks navigation. Users can switch tabs/edit while a job runs; returning reloads the durable state. Completion does not auto-open details or move focus. Failed processing can retry the stored file. Only the short transfer/acceptance waits in the upload control.

## Local V1 additions

Business owners register publicly without invitation, verify email via Mailpit and administer their new organization. Existing-business invitations preserve the email-bound credential flow; pending bot access activates only for the accepted invitation generation. The API tab manages chat-only assistant keys and an opt-in origin-restricted widget that reuses ChatScreen. Account includes Consumption, manual cost scenarios and 30-day Trash. All UI text ships in EN/ES/VI; Compose disables remote catalog requests. Usage displays actual token measurements priced by explicitly simulated Fyr Usage rates, without payments. See ../V1_LOCAL.md and ../REVISION_LOCAL.md for scope and review instructions.
