import { useState } from "react";
import type { FormEvent } from "react";
import type { Translate } from "../../i18n";
import type { CatalogInspection, CatalogMapping } from "../../services/spreadsheet-catalog";
import { catalogMappingSuggestions } from "../../services/catalog-mapping-suggestions";

export default function CatalogMappingEditor({ inspection, headerRow, busy, t, onPreview, showCachedFormulas = true }: { inspection: CatalogInspection; headerRow: number; busy: boolean; t: Translate; onPreview: (mapping: CatalogMapping) => Promise<void>; showCachedFormulas?: boolean }) {
  const [sameProduct, setSameProduct] = useState(true);
  const suggested = catalogMappingSuggestions(inspection.headers);
  const [freshness, setFreshness] = useState(suggested.observed ? "column" : "");
  const [price, setPrice] = useState(suggested.price);
  const [stock, setStock] = useState(suggested.stock);
  const options = inspection.headers.filter(Boolean);
  const field = (name: string, label: Parameters<Translate>[0], required = false) => <label className="field workspace-picker">{t(label)}<select name={name} required={required} defaultValue={suggested[name as keyof typeof suggested] ?? ""}>
    <option value="">{t(required ? "catalog.choose" : "catalog.not_mapped")}</option>{options.map(value => <option key={value} value={value}>{value}</option>)}
  </select></label>;
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); const form = new FormData(event.currentTarget);
    const column = (name: string) => String(form.get(name) ?? "") || null;
    await onPreview({ headerRow,
      columns: { externalId: column("id")!, name: column("name")!, productId: sameProduct ? null : column("product"), sku: column("sku"), presentation: null, unit: null, price: price || null, currency: null, available: stock || null, reserved: null, active: null, observedAt: freshness === "column" ? column("observed") : null },
      defaults: { productIdFromExternalId: sameProduct, presentation: String(form.get("presentation")).trim(), unit: String(form.get("unit")).trim(), currency: price ? String(form.get("currency")).trim().toUpperCase() : null, active: form.get("active") === "on", taxTreatment: String(form.get("tax") ?? "unspecified") },
      freshnessBasis: freshness || "column", allowCachedFormulas: form.get("formulas") === "on" });
  }
  return <form className="connection-editor" onSubmit={event => void submit(event)}>
    <h4>{t("catalog.mapping")}</h4><p className="panel-note">{t("catalog.identity_help")}</p>
    <fieldset disabled={busy}>
      {field("id", "catalog.external_id", true)}{field("name", "catalog.name", true)}{field("sku", "catalog.sku")}
      <label className="connection-modes"><input type="checkbox" checked={sameProduct} onChange={event => setSameProduct(event.target.checked)} />{t("catalog.same_product")}</label>
      {!sameProduct && field("product", "catalog.product_id", true)}
      <label className="field">{t("catalog.presentation")}<input name="presentation" required maxLength={256} /></label>
      <label className="field">{t("catalog.unit")}<input name="unit" required maxLength={32} pattern="[A-Za-z][A-Za-z0-9._-]*" placeholder="EA" /></label>
      <label className="field workspace-picker">{t("catalog.price")}<select value={price} onChange={event => setPrice(event.target.value)}><option value="">{t("catalog.not_mapped")}</option>{options.map(value => <option key={value}>{value}</option>)}</select></label>
      {price && <><label className="field">{t("catalog.currency")}<input name="currency" required minLength={3} maxLength={3} pattern="[A-Za-z]{3}" /></label>
        <label className="field workspace-picker">{t("catalog.tax")}<select name="tax" defaultValue="unspecified"><option value="unspecified">{t("records.tax_unspecified")}</option><option value="inclusive">{t("records.tax_inclusive")}</option><option value="exclusive">{t("records.tax_exclusive")}</option></select></label></>}
      <label className="field workspace-picker">{t("catalog.stock")}<select value={stock} onChange={event => setStock(event.target.value)}><option value="">{t("catalog.not_mapped")}</option>{options.map(value => <option key={value}>{value}</option>)}</select></label>
      <label className="connection-modes"><input name="active" type="checkbox" defaultChecked />{t("catalog.active")}</label>
      {(price || stock) && <><label className="field workspace-picker">{t("catalog.freshness")}<select required value={freshness} onChange={event => setFreshness(event.target.value)}><option value="">{t("catalog.choose")}</option><option value="column">{t("catalog.source_time")}</option><option value="read_at">{t("catalog.read_time")}</option></select></label>
        {freshness === "column" && field("observed", "catalog.observed", true)}
        {freshness === "read_at" && <p className="panel-note">{t("catalog.read_time_help")}</p>}</>}
      {showCachedFormulas && <label className="connection-modes"><input name="formulas" type="checkbox" />{t("catalog.formulas")}</label>}
      <button type="submit" className="secondary-button">{t(busy ? "workspace.loading" : "catalog.preview")}</button>
    </fieldset>
  </form>;
}
