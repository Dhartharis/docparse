import { useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import type { Language, MessageKey, Translate } from "../../i18n";
import { ApiError } from "../../services/api";
import { formatSourceDecimal } from "../../services/operational-data";
import { inspectSpreadsheet, previewSpreadsheet } from "../../services/spreadsheet-catalog";
import type { CatalogInspection, CatalogMapping, CatalogPreview } from "../../services/spreadsheet-catalog";
import CatalogMappingEditor from "./CatalogMappingEditor";

export default function SpreadsheetPreviewPanel({ organizationId, language, t }: { organizationId: string; language: Language; t: Translate }) {
  const [file, setFile] = useState<File | null>(null);
  const [sheet, setSheet] = useState("");
  const [header, setHeader] = useState(1);
  const [inspection, setInspection] = useState<CatalogInspection | null>(null);
  const [preview, setPreview] = useState<CatalogPreview | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);
  const controller = useRef<AbortController | null>(null);
  useEffect(() => () => controller.current?.abort(), []);
  async function act(operation: (file: File, signal: AbortSignal) => Promise<void>) {
    if (!file || controller.current) return;
    if (file.size > 10 * 1024 * 1024) { setError("errors.catalog_limit"); return; }
    const active = new AbortController(); controller.current = active; setBusy(true); setError(null); setPreview(null);
    try { await operation(file, active.signal); }
    catch (failure) { if (!active.signal.aborted) setError(failure instanceof ApiError ? `errors.${failure.code}` : "errors.server"); }
    finally { controller.current = null; if (!active.signal.aborted) setBusy(false); }
  }
  async function inspect(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    await act(async (selected, signal) => { const result = await inspectSpreadsheet(organizationId, selected, sheet, header, signal); if (!signal.aborted) { setInspection(result); setSheet(result.sheet); if (result.headersValid === false) setError("errors.catalog_headers"); } });
  }
  async function map(mapping: CatalogMapping) {
    await act(async (selected, signal) => { const result = await previewSpreadsheet(organizationId, selected, sheet, mapping, signal); if (!signal.aborted) setPreview(result); });
  }
  const issues = ["required", "text_identifier", "identifier", "duplicate_identifier", "decimal", "precision", "utc_timestamp", "currency", "boolean", "cached_formula", "cell_error", "cell_limit", "invalid_value"] as const;
  const fields: Record<string, MessageKey> = { externalId: "catalog.external_id", productId: "catalog.product_id", name: "catalog.name", sku: "catalog.sku", presentation: "catalog.presentation", unit: "catalog.unit", price: "catalog.price", available: "catalog.stock", reserved: "catalog.reserved", observedAt: "catalog.observed", currency: "catalog.currency", active: "catalog.active", offer: "catalog.row" };
  return <section className="connection-editor" aria-busy={busy}>
    <h3>{t("catalog.title")}</h3><p className="panel-note">{t("catalog.help")}</p>
    {error && <p role="alert">{t(error)}</p>}
    <form onSubmit={event => void inspect(event)}><fieldset disabled={busy}>
      <label className="field">{t("catalog.file")}<input type="file" accept=".xlsx" required onChange={event => { setFile(event.target.files?.[0] ?? null); setSheet(""); setInspection(null); setPreview(null); }} /></label>
      {inspection && <label className="field workspace-picker">{t("catalog.sheet")}<select value={sheet} onChange={event => { setSheet(event.target.value); setPreview(null); }}>{inspection.sheets.map(value => <option key={value}>{value}</option>)}</select></label>}
      <label className="field">{t("catalog.header_row")}<input type="number" required min={1} max={50} step={1} value={header} onChange={event => { setHeader(Number(event.target.value)); setInspection(null); setPreview(null); }} /></label>
      <button type="submit" className="secondary-button">{t("catalog.inspect")}</button>
    </fieldset></form>
    {inspection && inspection.headersValid !== false && inspection.sheet === sheet && <CatalogMappingEditor key={JSON.stringify([sheet, header, inspection.headers])} inspection={inspection} headerRow={header} busy={busy} t={t} onPreview={map} />}
    {preview && <div aria-live="polite">
      <p>{t(preview.canImport ? "catalog.valid" : "catalog.invalid", { count: preview.rowCount })}</p>
      {preview.issues.length > 0 && <ul>{preview.issues.map((issue, index) => { const code = issues.find(value => value === issue.code) ?? "invalid_value"; return <li key={index}>{t("catalog.issue", { row: issue.row, field: t(fields[issue.field] ?? "catalog.row"), message: t(`catalog.issue_${code}`) })}</li>; })}</ul>}
      <ul className="record-list">{preview.offers.map(offer => <li key={offer.externalId}><h4>{offer.name}</h4><p>{offer.externalId} · {offer.presentation}</p><p>{offer.price.known && offer.price.amount !== null ? `${formatSourceDecimal(offer.price.amount, language)} ${offer.price.currency}` : t("records.unknown")}</p><p>{offer.availability.available.known && offer.availability.available.quantity !== null ? `${formatSourceDecimal(offer.availability.available.quantity, language)} ${offer.unitCode}` : t("records.unknown")}</p></li>)}</ul>
    </div>}
  </section>;
}
