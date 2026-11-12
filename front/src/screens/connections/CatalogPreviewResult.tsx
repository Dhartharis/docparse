import type { Language, MessageKey, Translate } from "../../i18n";
import { formatSourceDecimal } from "../../services/operational-data";
import type { CatalogPreview } from "../../services/spreadsheet-catalog";

export default function CatalogPreviewResult({ preview, language, t }: { preview: CatalogPreview; language: Language; t: Translate }) {
  const issues = ["required", "text_identifier", "identifier", "duplicate_identifier", "decimal", "precision", "utc_timestamp", "currency", "boolean", "cached_formula", "cell_error", "cell_limit", "invalid_value"] as const;
  const fields: Record<string, MessageKey> = { externalId: "catalog.external_id", productId: "catalog.product_id", name: "catalog.name", sku: "catalog.sku", presentation: "catalog.presentation", unit: "catalog.unit", price: "catalog.price", available: "catalog.stock", reserved: "catalog.reserved", observedAt: "catalog.observed", currency: "catalog.currency", active: "catalog.active", offer: "catalog.row" };
  return (<div aria-live="polite">
      <p>{t(preview.canImport ? "catalog.valid" : "catalog.invalid", { count: preview.rowCount })}</p>
      {preview.issues.length > 0 && <ul>{preview.issues.map((issue, index) => { const code = issues.find(value => value === issue.code) ?? "invalid_value"; return <li key={index}>{t("catalog.issue", { row: issue.row, field: t(fields[issue.field] ?? "catalog.row"), message: t(`catalog.issue_${code}`) })}</li>; })}</ul>}
      <ul className="record-list">{preview.offers.map(offer => <li key={offer.externalId}><h4>{offer.name}</h4><p>{offer.externalId} · {offer.presentation}</p><p>{offer.price.known && offer.price.amount !== null ? `${formatSourceDecimal(offer.price.amount, language)} ${offer.price.currency}` : t("records.unknown")}</p><p>{offer.availability.available.known && offer.availability.available.quantity !== null ? `${formatSourceDecimal(offer.availability.available.quantity, language)} ${offer.unitCode}` : t("records.unknown")}</p></li>)}</ul>
    </div>);
}
