import { api } from "./api";
import type { SourceConnection } from "./connections";

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
export type LiveResponse = { queriedAt: string; results: { selectorValue: string; status: "found" | "ambiguous" | "not_found" | "unavailable"; records: OperationalRecord[] }[] };
export type SyncStatus = { state: "waiting" | "processing" | "failed" | "paused"; attempts: number; dueAt: string; errorCode: string | null; lastSuccessAt: string | null; lastCompletedAt: string | null; snapshotCompleted: boolean };
export const readSyncStatus = (org: string, connection: string, signal: AbortSignal) => api<SyncStatus>(`/organizations/${org}/connections/${connection}/sync`, undefined, signal);
export const restartSync = (org: string, connection: SourceConnection, signal: AbortSignal) => api(`/organizations/${org}/connections/${connection.id}/sync/restart`, { revision: connection.revision }, signal);
export const querySource = (org: string, connection: string, query: { selectorKind: string; values: string[]; fields: string[] }, signal: AbortSignal) =>
  api<LiveResponse>(`/organizations/${org}/connections/${connection}/query`, query, signal);
export const readOperationalRecords = (org: string, connection: string, signal: AbortSignal) =>
  api<OperationalSnapshot>(`/organizations/${org}/connections/${connection}/records`, undefined, signal);

// Keep the source's decimal precision; converting an 18+6 digit amount to Number loses data.
export function formatSourceDecimal(value: string, language: string): string {
  const [integer, fraction] = value.split(".");
  const grouped = new Intl.NumberFormat(language).format(BigInt(integer));
  const separator = new Intl.NumberFormat(language).formatToParts(1.1).find(part => part.type === "decimal")!.value;
  return grouped + (fraction ? separator + fraction : "");
}
