# Google Drive authorization and spreadsheet preview

## Customer flow and current scope

In Data connections, an Admin opens **Google Drive**, chooses **Connect Google**, authorizes access in a popup, and selects a Google Sheets or `.xlsx` file in Google Picker. The app reads worksheet/header information and reuses the catalog column mapper and exact-value preview. Existing authorizations can choose another file without another consent prompt while valid. Customers do not paste keys, make files public, create service accounts, or install scripts.

This feature saves the Google authorization, not a catalog source. File selections, mappings, preview rows and access tokens used by Picker stay in client memory. No catalog data is persisted, and no scheduled/live business-data source or bot permission is created. Automatic import, native local versioning and saved source configuration remain the next feature.

Each authorization belongs to the initiating DocParse user within one organization. Other Admins cannot obtain its tokens or inspect its files. Accounts are displayed by connection date; more than one authorization is allowed. Organization/user membership foreign keys cascade local authorization deletion when membership is removed. Admin demotion denies subsequent requests; results recheck current Admin access after external I/O. Sharing a future saved catalog with bots must use separate explicit source/tool grants.

Google's `drive.file` permission is limited to files opened/selected for the app and also permits writes. DocParse implements only read operations; the UI describes the permission accurately. No upload view is added to Picker. Selected file IDs are verified again server-side (supported MIME, app authorization, download capability and non-trashed state); there is no arbitrary URL or public-sharing fallback.

## Authorization and credential lifecycle

- Default disabled. Configuration is server-side; the browser receives only the restricted Picker API key, project number and an ephemeral access token required by Picker. The OAuth client secret and refresh token never reach the client.
- A cryptographically random state and PKCE verifier are created for each authorization. Only the state hash and a protected verifier are stored, bound to organization, user and new authorization identity; expiry is ten minutes.
- Code redemption atomically claims state before token I/O, erases the persisted verifier and performs one exchange. Concurrent requests cannot redeem twice. A retry after a completed exchange returns the existing identity; a failed/interrupted claimed exchange requires a new Connect Google flow rather than replaying an uncertain authorization code.
- Tokens are protected with ASP.NET Data Protection purposes containing organization, user, account and credential purpose. Key-ring I/O occurs outside short database transactions. Use durable, encrypted shared Data Protection keys in production, as required by existing connection secrets.
- Token access uses a short durable lease. Tokens with over six minutes remaining (enough for the five-minute Picker deadline) are reused; otherwise an offline refresh runs outside the transaction. The lease expires after 30 seconds. A fresh token is returned only after a fenced save and current-configuration check. Failed or uncertain refreshes require fresh authorization; an abandoned lease is never silently replayed. Provider expiry of the refresh token is honored.
- Disconnect removes local tokens, changes revision and fences in-flight authorization/refresh/file results. It does not globally revoke the Google grant: revocation could affect another organization or authorization using the same Google client. The UI points to Google Account permissions for optional global removal. Local list/disconnect remains available when the operator disables the integration.
- Expired pending state is purged on the next authorization by its owner; pending/tokenless authorization rows older than one day are pruned then. Connected credentials remain until disconnect or membership removal.

The popup preserves the parent's existing in-memory DocParse session. A dedicated frontend callback strips the query immediately, uses bundled localized text, sends only to the exact same-origin opener and has restrictive CSP/no-referrer/no-store headers. The parent verifies source window, origin and exact state before authenticated completion. A cancelled/closed/expired/blocked popup shows a localized recovery action. No token or authorization code is stored in browser storage. Browsers that sever popup opener relationships must be checked during real Google acceptance; a missing opener asks the customer to return and reconnect.

## Reading guarantees

Provider endpoints are fixed to Google HTTPS origins in production; only explicit loopback/host.docker.internal HTTP fixtures are allowed in Development. Requests use a dedicated client without default credential/body logging, redirects, proxies, cookies or decompression. Each complete HTTP response is bounded by ten seconds and a byte limit; a complete file read has a 25-second deadline.

Drive metadata is fetched before and after reading. Any change to identity/name/MIME/version/modification time rejects the preview rather than mixing versions. `.xlsx` downloads use the bounded read-only reader from the previous feature. Native Sheets requests use complete worksheet Values ranges, UNFORMATTED_VALUE and explicit date rendering. Empty allocated rows are not confused with a truncated successful read; response/row/column limits fail rather than truncate. Sheet type defaults to GRID per the provider contract; object/data-source sheets are excluded. Native Sheets formula values are already calculated by Google, while cached Excel formulas require explicit opt-in. Existing source observation/read-time policies and money/identifier rules remain unchanged.

See [spreadsheet mapping rules and limits](spreadsheet-catalog.md). Google metadata versions are consistency evidence here, not operational offer versions. No projection is updated by these endpoints.

## One-time operator setup

1. Use a Google Cloud project owned by Fyr Studio, enable Google Drive API, Google Sheets API and Google Picker API, and configure OAuth branding/audience/consent with the published app domain, privacy information and `https://www.googleapis.com/auth/drive.file`.
2. Create a **Web application** OAuth client. Register the exact redirect `https://docparse.fyr-studio.com/google-drive-callback.html` (or the explicit local frontend callback for local acceptance). The callback origin must equal `PublicWeb:BaseUrl`; HTTPS is required outside Development.
3. Create the browser Picker API key in the same project. Restrict it to the actual frontend HTTP referrers and required Google Picker API, and use that project's numeric project number for Picker app ID. Use the OAuth client and API key from this same project.
4. Set server configuration, using secrets/environment variables rather than source files:

| Setting | Purpose |
|---|---|
| `GoogleDrive__Enabled` | `true` after all configuration is present; default `false` |
| `GoogleDrive__ClientId` | Web OAuth client ID |
| `GoogleDrive__ClientSecret` | Server-only client secret |
| `GoogleDrive__PickerApiKey` | Referrer/API-restricted browser Picker key |
| `GoogleDrive__ProjectNumber` | Numeric Google Cloud project number |
| `GoogleDrive__RedirectUri` | Exact registered frontend callback URI |
| `PublicWeb__BaseUrl` | Actual frontend origin |

Local Compose accepts matching `GOOGLE_DRIVE_*` variables and `PUBLIC_WEB_BASE_URL`. Do not put credentials in a PR or chat. Apply migration 022 before serving traffic. Publish both frontend entry pages/assets and the callback security headers together with the backend when release is separately authorized.

Google consent/audience restrictions, testing-mode token lifetime, Workspace administrator policies, real popup/opener behavior and actual browser API-key restrictions must be verified with the configured project. Those checks are blocked until real Google project credentials are supplied; fixture success is not production Google acceptance. No account was provisioned, grant revoked or deployment performed during implementation.

## Validation

- `dotnet build back/DocParse.csproj`
- `python tests/check_backend_boundaries.py`
- `dotnet run --project tests/GoogleDrivePolicyTests/GoogleDrivePolicyTests.csproj`
- `npm --prefix front run build` / `npm --prefix front run lint`
- `npm --prefix front test -- --workers=2`
- `python tests/google_drive_integration.py --google-only --browser` for an isolated Google-only fixture loop
- `python tests/google_drive_integration.py --browser` for Push/live/scheduled/XLSX/Google fixtures together, including controlled-clock PostgreSQL refresh/lease checks and real API browser journeys
- `python tests/backend_integration.py` with a unique branch image override for the full identity/RAG/Blob/widget/schema regression

Primary provider contracts: [server OAuth](https://developers.google.com/identity/protocols/oauth2/web-server), [PKCE](https://developers.google.com/identity/protocols/oauth2/native-app#step1-code-verifier), [Picker](https://developers.google.com/workspace/drive/picker/guides/web-picker-sample), [Drive file reads](https://developers.google.com/workspace/drive/api/reference/rest/v3/files/get), [Sheets Values](https://developers.google.com/workspace/sheets/api/reference/rest/v4/spreadsheets.values/get), [sheet type defaults](https://developers.google.com/workspace/sheets/api/reference/rest/v4/spreadsheets/sheets#SheetProperties).
