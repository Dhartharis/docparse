import { api } from "./api";
import type { OperationalRecord } from "./operational-data";

export type CatalogInspection = { sheet: string; sheets: string[]; headers: string[]; rowCount: number; headersValid?: boolean };
export type CatalogMapping = {
  headerRow: number;
  columns: { externalId: string; name: string; productId: string | null; sku: string | null; presentation: string | null; unit: string | null; price: string | null; currency: string | null; available: string | null; reserved: string | null; active: string | null; observedAt: string | null };
  defaults: { productIdFromExternalId: boolean; presentation: string; unit: string; currency: string | null; active: boolean; taxTreatment: string };
  freshnessBasis: string; allowCachedFormulas: boolean;
};
export type CatalogPreview = CatalogInspection & { offers: OperationalRecord["offer"][]; issues: { row: number; field: string; code: string }[]; canImport: boolean };
function upload(file: File, sheet: string, headerRow: number) {
  const form = new FormData(); form.set("file", file); form.set("sheet", sheet); form.set("headerRow", String(headerRow)); return form;
}
export const inspectSpreadsheet = (org: string, file: File, sheet: string, headerRow: number, signal: AbortSignal) =>
  api<CatalogInspection>(`/organizations/${org}/spreadsheet-catalog/inspect`, upload(file, sheet, headerRow), signal);
export const previewSpreadsheet = (org: string, file: File, sheet: string, mapping: CatalogMapping, signal: AbortSignal) => {
  const form = upload(file, sheet, mapping.headerRow); form.set("mapping", JSON.stringify(mapping));
  return api<CatalogPreview>(`/organizations/${org}/spreadsheet-catalog/preview`, form, signal);
};
