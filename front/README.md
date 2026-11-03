# DocParse frontend

React + TypeScript + Vite client. The initial delivery is a login interface preview, not an authentication implementation.

## Run locally

Requires a Node version supported by Vite (developed with Node 24).

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

Browser tests cover required-field validation, keyboard interaction, password visibility, preview credential isolation, language selection/fallback, remote catalogs, offline cache, malformed packs, responsive overflow, and automated accessibility scans in English/Spanish/Vietnamese at 320, 390, 768, and 1440 pixels. Automated tests intercept localization requests and never depend on production services. Automated scans are not a full accessibility certification.

## Scope and source of truth

- Product: [DocParse — Product Definition & Pricing](https://docs.google.com/document/d/1ao033DtGdlLEpfZSGLFziUoYwa5ztnBSh7mcjROCITE/edit).
- React follows the product definition. Vite provides a small client-side build with no SSR or routing infrastructure yet.
- Visual reference: `C:\Proyectos\fyr-portal\DESIGN.md` and its current template. Product and studio SVG assets were copied unchanged from that repository; gold remains the product mark, violet the interaction accent.
- Target application URL: `https://docparse.fyr-studio.com/`, per the current instruction. The product document still records the older domain; the portal now links to the current domain.
- English is the complete bundled fallback; Spanish and Vietnamese are also bundled. Only the explicit language preference and validated public translation packs are persisted. Credential fields remain in the form and are neither sent nor persisted by the application.
- Submit validates required fields and reports that authentication is unavailable. There are no fake sessions, role selectors, dashboard redirects, registration, or password-reset flows.

## Integration still required

Define the backend authentication/session contract, account provisioning and password recovery before connecting login. Roles must come from trusted server state: Admin enters administration; Viewer enters chat directly. Role enforcement must also exist on the backend.

`npm run build` writes static files to `front/dist`. Hosting must serve this directory over HTTPS. The user has confirmed the application domain is configured. Root and `/login` can serve the same application using a host SPA fallback. After the directory reorganization, the hosting source/app directory must be `front` and its build output `dist`; this checkout has no hosting workflow or configuration file to update.

This delivery does not change the DocParse API, provision DNS, deploy application/server code, or update the product document/portal. The .NET project is isolated in `../back/DocParse.csproj`; each application owns its own source and build outputs.

## Fyr Localization integration

Public endpoint: `https://fyr-localization-production.up.railway.app/v1/products`.

- Login strings use `docparse/locales` and `docparse/locales/{locale}`. The authoring files under `localization/docparse` follow the existing service publishing contract. EN/ES/VI version 1 was published and read back from the public API on November 3, 2026.
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
- No localization strings, authentication behavior, portal files, deployments or dependencies change for this transition.

Browser tests use controlled animation time and cover initial color, completion/cleanup, reload behavior, language preservation, direct/reduced-motion visits, mobile target selection, resize/keyboard cancellation and image-load failure. Desktop intermediate frames and the completed screen were also visually inspected.

## GitHub deployment

Workflow: `../.github/workflows/deploy-front.yml`. Pull requests targeting `main` validate the frontend; pushes to `main` affecting `front/` or the workflow validate and deploy it. Manual workflow dispatch on `main` is also supported.

The workflow uses Node 24, `npm ci`, lint, build/catalog validation and Playwright Chromium tests. It uploads only `front/dist` to the existing Azure Static Web App `fyr-docparse`, with both automatic app build and API build disabled in the Azure upload action. No backend is built or deployed.

GitHub repository: `Dhartharis/docparse`. Required Actions repository secret: `AZURE_STATIC_WEB_APPS_API_TOKEN_DOCPARSE`, containing the deployment token of `fyr-docparse` (not the portal's token). Keep that credential in encrypted GitHub Actions secrets, never in source, frontend environment variables or build output.

`public/staticwebapp.config.json` is copied by Vite to the output root. It enables SPA navigation fallback, excludes asset/API/development-proxy paths from fallback, and asks clients to revalidate `index.html` so hashed bundles can update. The production domain is `https://docparse.fyr-studio.com/` and the Azure host is `https://lemon-dune-02b120610.4.azurestaticapps.net`.

The legacy backend relocation and root documentation may remain local pending separate backend work; this frontend deployment includes only `front/` and its workflow.
