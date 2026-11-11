import { api, ApiError } from "./api";

export type BillingStatus = "none" | "incomplete" | "incomplete_expired" | "trialing" | "active" | "past_due" | "canceled" | "unpaid" | "paused";
export type BillingSummary = {
  enabled: boolean;
  mode: "test";
  availablePlans: ("starter" | "pro")[];
  state: { status: BillingStatus; planKey: string | null; hasCustomer: boolean; pendingEvents: number; failedEvents: number; checkoutPlanKey: string | null };
};
const root = (org: string) => `/organizations/${org}/billing`;
export const loadBilling = (org: string, signal?: AbortSignal) => api<BillingSummary>(root(org) + "/", undefined, signal);
export const retryBilling = (org: string, signal?: AbortSignal) => api(root(org) + "/retry", {}, signal);

function trustedPaymentUrl(raw: string, host: string): string {
  try {
    const url = new URL(raw);
    if (url.protocol === "https:" && url.hostname === host && !url.username && !url.password && !url.port) return url.href;
  } catch { /* Translate malformed provider output at this boundary. */ }
  throw new ApiError("server");
}

export async function openCheckout(org: string, planKey: string, requestId: string, signal?: AbortSignal) {
  const result = await api<{ url: string }>(root(org) + "/checkout", { planKey, requestId }, signal);
  return trustedPaymentUrl(result.url, "checkout.stripe.com");
}
export async function openBillingPortal(org: string, signal?: AbortSignal) {
  const result = await api<{ url: string }>(root(org) + "/portal", {}, signal);
  return trustedPaymentUrl(result.url, "billing.stripe.com");
}
