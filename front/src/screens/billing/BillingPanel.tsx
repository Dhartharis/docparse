import { useCallback, useEffect, useRef, useState } from "react";
import type { Language, MessageKey, Translate } from "../../i18n";
import { ApiError } from "../../services/api";
import { loadBilling, openBillingPortal, openCheckout, retryBilling } from "../../services/billing";
import type { BillingSummary } from "../../services/billing";

export default function BillingPanel({ organizationId, t, language }: { organizationId: string; t: Translate; language: Language }) {
  const [summary, setSummary] = useState<BillingSummary | null>(null);
  const [plan, setPlan] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);
  const controller = useRef<AbortController | null>(null);
  const requestId = useRef(crypto.randomUUID());
  const load = useCallback(async (signal: AbortSignal) => {
    try {
      const value = await loadBilling(organizationId, signal);
      if (!signal.aborted) {
        setSummary(value);
        if (!value.state.checkoutPlanKey) requestId.current = crypto.randomUUID();
        setPlan(previous => {
          const current = !["none", "canceled", "incomplete_expired"].includes(value.state.status) ? value.state.planKey : null;
          return current ?? value.state.checkoutPlanKey ?? (value.availablePlans.includes(previous as "starter" | "pro") ? previous : (value.availablePlans[0] ?? ""));
        });
        setError(null);
      }
    } catch (failure) {
      if (!signal.aborted) setError(failure instanceof ApiError ? `errors.${failure.code}` : "errors.server");
    }
  }, [organizationId]);
  useEffect(() => {
    const active = new AbortController();
    controller.current = active;
    void Promise.resolve().then(() => { if (!active.signal.aborted) return load(active.signal); });
    return () => active.abort();
  }, [load]);
  useEffect(() => {
    if (!summary?.enabled) return;
    const timer = setInterval(() => { if (!document.hidden && controller.current && !busy) void load(controller.current.signal); }, 15000);
    return () => clearInterval(timer);
  }, [load, summary?.enabled, busy]);
  async function act(action: "checkout" | "portal" | "retry" | "refresh") {
    if (busy || !controller.current) return;
    const signal = controller.current.signal;
    setBusy(true); setError(null);
    try {
      if (action === "checkout") {
        const url = await openCheckout(organizationId, plan, requestId.current, signal);
        if (!signal.aborted) window.location.assign(url);
      } else if (action === "portal") {
        const url = await openBillingPortal(organizationId, signal);
        if (!signal.aborted) window.location.assign(url);
      } else {
        if (action === "retry") await retryBilling(organizationId, signal);
        await load(signal);
      }
    } catch (failure) {
      if (!signal.aborted) setError(failure instanceof ApiError ? `errors.${failure.code}` : "errors.server");
    } finally { if (!signal.aborted) setBusy(false); }
  }
  const number = (value: number) => new Intl.NumberFormat(language).format(value);
  const mayCheckout = summary?.state.status === "none" || summary?.state.status === "canceled" || summary?.state.status === "incomplete_expired";
  return <section className="usage-panel" aria-busy={busy}>
    <p className="usage-simulation">{t("billing.test_notice")}</p>
    {error && <p className="workspace-alert" role="alert">{t(error)}</p>}
    {!summary && !error && <p role="status">{t("billing.loading")}</p>}
    {summary && <>
      {!summary.enabled ? <p className="panel-note">{t("billing.not_configured")}</p> : <>
        <dl className="billing-status"><div><dt>{t("billing.subscription")}</dt><dd>{t(`billing.status_${summary.state.status}`)}</dd></div></dl>
        <p className="panel-note">{t("billing.pending", { count: number(summary.state.pendingEvents) })}</p>
        {summary.state.failedEvents > 0 && <p role="status">{t("billing.failed_sync")}</p>}
        {summary.availablePlans.length > 0 ? <label className="field">{t("billing.plan")}
          <select value={plan} disabled={busy || !mayCheckout || Boolean(summary.state.checkoutPlanKey)} onChange={event => { setPlan(event.target.value); requestId.current = crypto.randomUUID(); }}>
            {summary.availablePlans.map(key => <option key={key} value={key}>{t(`billing.plan_${key}`)}</option>)}
          </select>
        </label> : <p className="panel-note">{t("billing.no_prices")}</p>}
        <div className="billing-actions">
          <button className="submit-button" type="button" disabled={busy || !plan || !mayCheckout} onClick={() => void act("checkout")}>{t("billing.checkout")}</button>
          <button className="secondary-button" type="button" disabled={busy || !summary.state.hasCustomer} onClick={() => void act("portal")}>{t("billing.portal")}</button>
          {summary.state.failedEvents > 0 && <button className="secondary-button" type="button" disabled={busy} onClick={() => void act("retry")}>{t("common.retry")}</button>}
        </div>
      </>}
    </>}
    <button className="secondary-button" type="button" disabled={busy} onClick={() => void act("refresh")}>{t("billing.refresh")}</button>
  </section>;
}
