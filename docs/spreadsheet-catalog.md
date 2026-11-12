# Spreadsheet catalog mapping

## Delivered scope

Admin users can inspect a local `.xlsx` file in **Data connections → Preview spreadsheet**, choose a worksheet/header row, review suggested columns and validate a catalog mapping. This is a preview only: it does not persist files or offers, create a source, grant chatbot access, or connect a Google account. The reader also supports native Google Sheets Values responses for the upcoming authenticated Drive adapter; that path is currently covered by fixtures, not a user-facing Google connection.

The flow is available in English, Spanish and Vietnamese. Suggestions match a unique known header; ambiguous columns require explicit selection. The Admin reviews identifiers, product grouping, presentation, quantity unit, currency, tax treatment and freshness policy. This first editor uses constants for presentation/unit/currency and separate optional product/SKU columns; the mapping contract can also bind those fields per row for a future editor.

## Parsing and mapping guarantees

- IDs must be text, preserving leading zeros. Stable external/product identifiers follow the operational contract; SKU allows Unicode text. Each offer requires a product identity, name, presentation and explicit quantity unit.
- Money and quantities preserve exact decimal strings, with at most 18 integer and six fractional digits. Numeric spreadsheet cells exceeding 15 significant digits are rejected: the client must store such values as text. Exponents are normalized by string arithmetic; underflow is never rounded to zero. Localized separators are rejected rather than guessed.
- A blank amount is unknown, while `0` is known zero. Available stock must already exclude reservations and be expressed in the declared quantity unit. Currency is explicit, uppercase, three letters; this preview checks the wire contract shape, not ISO registry membership.
- Known price/stock requires a source UTC timestamp column or an explicit policy to use the DocParse read time. Reading a file does not establish when physical stock or price was last verified; the UI explains this trade-off. Connection limits determine expiry from the selected observation timestamp.
- All nonempty rows are validated, with at most ten valid offers and 100 issues displayed. Any invalid/duplicate identifier makes `canImport` false; partial previews never imply a valid complete import. Empty formula rows are not silently dropped. A valid empty sheet is only a preview result; future import must explicitly confirm destructive empty snapshots.
- Cached XLSX formula results require explicit opt-in and may be stale. DocParse does not calculate formulas or fetch external workbook links. Native Sheets values arrive as provider-calculated values; native source setup must explain that distinction.
- The API reuses `IOperationalProjection.Validate` for offer semantics without mutating the projection. Preview timestamps/settings are temporary (price 600s, stock 60s, skew 0); a future saved source uses its own settings and file modification metadata.

Limits: 10 MiB XLSX; 16 MiB Sheets JSON; 5,000 data rows, header row 1–50, 128 columns, 4,096 Unicode scalars per cell; at most 100 worksheets. XLSX ZIP parts, total expansion, duplicate names, macros, row/cell references and text totals are bounded. SDK parsing is read-only and cancellation bounded. Unknown sheets, malformed files and mapping failures return safe localized codes, without cell contents. Uploads remain in request memory and are not saved to disk or logs. Admin authorization is tenant scoped; bearer authentication is required; responses use `no-store` and existing request rate limits.

`DocumentFormat.OpenXml` 3.5.1 (MIT) provides the XLSX reader. The existing EPPlus export implementation is unchanged and still needs its separate commercial license review.

## Google Drive connector: accepted direction and remaining implementation

The customer flow is **Connect Google → choose a file → review columns → preview → save the source**. Support both native Google Sheets and `.xlsx` in Drive, with no public sharing or customer service-account setup. Google Picker and the selected-file `drive.file` scope provide file-limited authorization; the scope permits writes, but DocParse operations remain read-only. OAuth client/cloud configuration will be supplied later; secrets must never be placed in frontend storage or chat.

The Google-specific follow-up needs server-side encrypted OAuth credentials, account/org-bound short-lived authorization state, offline refresh, a safe popup callback, Picker selection validated again on the server, disconnect/reconnect and an injectable provider boundary. Access tokens needed by Picker live only in client memory. All external calls occur outside database transactions; durable claims and configuration fences prevent in-flight revocation from committing results.

Google files do not expose the external provider's operational change feed. The native adapter must read a complete bounded table, reject inconsistent or invalid reads without replacing the previous catalog, and assign durable local monotonic versions to normalized offers. It must integrate with on-demand and scheduled reads through common operational contracts, without inventing provider cursors or routing requests back through DocParse HTTP. Snapshot absence takes effect only after complete validated import. Reconfiguration/reconnection must fence old work and preserve version ordering. No bot or public access is implied by source setup.

Primary provider references: [selected-file scope](https://developers.google.com/workspace/drive/api/guides/api-specific-auth), [Picker](https://developers.google.com/workspace/drive/picker/guides/overview), [Sheets Values API](https://developers.google.com/workspace/sheets/api/reference/rest/v4/spreadsheets.values/get), [binary downloads](https://developers.google.com/workspace/drive/api/guides/manage-downloads).

## Validation

- `dotnet build back/DocParse.csproj`
- `python tests/check_backend_boundaries.py`
- `dotnet run --project tests/CatalogPolicyTests/CatalogPolicyTests.csproj`
- `npm --prefix front run build` / `npm --prefix front run lint`
- `npm --prefix front test -- --workers=2`
- `python tests/catalog_integration.py --browser` (isolated database, simulated operational provider, real API/XLSX/browser; no Google credentials)
- `python tests/backend_integration.py` with an isolated branch image override

Real Google consent, file selection and background reads remain unvalidated until the adapter and Google project are configured. CI exercises the parser/preview and existing operational connector fixtures; no deployment is triggered.
