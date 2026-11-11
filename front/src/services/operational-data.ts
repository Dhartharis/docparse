import { api } from "./api";

export type Quantity = { known: boolean; quantity: string | null; reason: string | null };
export type OperationalRecord = {
  offer: {
    externalId: string; productId: string; sku: string | null; name: string; presentation: string; unitCode: string;
    version: string; updatedAt: string; active: boolean;
    price: { known: boolean; amount: string | null; currency: string | null; taxTreatment: string | null; observedAt: string | null; validUntil: string | null };
    availability: { tracking: string; available: Quantity; reserved: Quantity; observedAt: string; validUntil: string };
  };
  priceCurrent: boolean; availabilityCurrent: boolean;
};
export type OperationalSnapshot = { total: number; records: OperationalRecord[] };
export const readOperationalRecords = (org: string, connection: string, signal: AbortSignal) =>
  api<OperationalSnapshot>(`/organizations/${org}/connections/${connection}/records`, undefined, signal);

// Keep the source's decimal precision; converting an 18+6 digit amount to Number loses data.
export function formatSourceDecimal(value: string, language: string): string {
  const [integer, fraction] = value.split(".");
  const grouped = new Intl.NumberFormat(language).format(BigInt(integer));
  const separator = new Intl.NumberFormat(language).formatToParts(1.1).find(part => part.type === "decimal")!.value;
  return grouped + (fraction ? separator + fraction : "");
}
