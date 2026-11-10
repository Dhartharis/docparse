import { useCallback, useEffect, useState } from "react";
import type { Language, MessageKey, Translate } from "../../i18n";
import { api, ApiError } from "../../services/api";
import type { Bot } from "../../services/api";
type CreditState = {
  planKey: string;
  includedMicrocredits: string;
  consumedMicrocredits: string;
  reservedMicrocredits: string;
  remainingMicrocredits: string;
  overageMicrocredits: string;
  estimatedOverageNanoUsd: string;
  creditValueNanoUsd: string;
  providerCostNanoUsd: string;
  canUseAi: boolean;
  allowOverage: boolean;
  simulation: boolean;
  resetsAt: string;
};
type Summary = {
  state: CreditState;
  nextPlan: string;
  local: { pendingOperations: number; failedSynchronizations: number };
};
type Breakdown = {
  resourceId: string | null;
  feature: string;
  operations: number;
  chargedMicrocredits: string;
  providerCostNanoUsd: string;
};
export default function UsagePanel({
  organizationId,
  t,
  language,
}: {
  organizationId: string;
  t: Translate;
  language: Language;
}) {
  const [summary, setSummary] = useState<Summary | null>(null);
  const [plans, setPlans] = useState<{ key: string }[]>([]);
  const [breakdown, setBreakdown] = useState<Breakdown[]>([]);
  const [bots, setBots] = useState<Bot[]>([]);
  const [error, setError] = useState<MessageKey | null>(null);
  const [busy, setBusy] = useState(false);
  const root = `/organizations/${organizationId}/usage`;
  const load = useCallback(
    async (signal?: AbortSignal) => {
      try {
        const [s, p, b, assistants] = await Promise.all([
          api<Summary>(root + "/", undefined, signal),
          api<{ key: string }[]>(root + "/plans", undefined, signal),
          api<Breakdown[]>(root + "/breakdown", undefined, signal),
          api<Bot[]>(
            `/organizations/${organizationId}/bots/`,
            undefined,
            signal,
          ),
        ]);
        if (!signal?.aborted) {
          setSummary(s);
          setPlans(p);
          setBreakdown(b);
          setBots(assistants);
          setError(null);
        }
      } catch (failure) {
        if (!signal?.aborted)
          setError(
            failure instanceof ApiError
              ? (`errors.${failure.code}` as MessageKey)
              : "errors.server",
          );
      }
    },
    [root, organizationId],
  );
  useEffect(() => {
    const controller = new AbortController();
    void Promise.resolve().then(() => { if (!controller.signal.aborted) return load(controller.signal); });
    const timer = setInterval(() => {
      if (!document.hidden) void load(controller.signal);
    }, 5000);
    return () => {
      controller.abort();
      clearInterval(timer);
    };
  }, [load]);
  async function selectPlan(planKey: string) {
    if (busy) return;
    setBusy(true);
    try {
      await api(root + "/plan", { planKey }, undefined, "PUT");
      await load();
    } catch {
      setError("errors.server");
    } finally {
      setBusy(false);
    }
  }
  const credits = (v: string) =>
    new Intl.NumberFormat(language, { maximumFractionDigits: 6 }).format(
      Number(v) / 1e6,
    );
  const usd = (v: string) =>
    new Intl.NumberFormat(language, {
      style: "currency",
      currency: "USD",
      maximumFractionDigits: 6,
    }).format(Number(v) / 1e9);
  return (
    <section className="usage-panel">
      {error && (
        <div className="workspace-alert" role="alert">
          <p>{t(error)}</p>
          <button type="button" onClick={() => void load()}>
            {t("common.retry")}
          </button>
        </div>
      )}
      {!summary && !error && <p role="status">{t("workspace.loading")}</p>}
      {summary && (
        <>
          {summary.state.simulation && (
            <p className="usage-simulation">{t("usage.simulation")}</p>
          )}
          <p>{t("usage.current_plan", { plan: summary.state.planKey })}</p>
          <div className="usage-metrics">
            {(
              [
                ["included", summary.state.includedMicrocredits],
                ["consumed", summary.state.consumedMicrocredits],
                ["reserved", summary.state.reservedMicrocredits],
                ["remaining", summary.state.remainingMicrocredits],
                ["overage", summary.state.overageMicrocredits],
              ] as const
            ).map(([key, value]) => (
              <div key={key}>
                <span>{t(`usage.${key}`)}</span>
                <strong>{credits(value)}</strong>
              </div>
            ))}
          </div>
          <p className="panel-note">
            {t("usage.credit_value", {
              value: usd(summary.state.creditValueNanoUsd),
            })}
          </p>
          <p>
            {t("usage.extra_estimate", {
              value: usd(summary.state.estimatedOverageNanoUsd),
            })}
          </p>
          {!summary.state.canUseAi && (
            <p role="status" className="workspace-alert">
              {t("errors.usage_limit")}
            </p>
          )}
          {summary.state.allowOverage && (
            <p className="panel-note">{t("usage.unlimited_help")}</p>
          )}
          <p className="panel-note">
            {t("usage.resets", {
              date: new Intl.DateTimeFormat(language, {
                dateStyle: "medium",
                timeStyle: "short",
              }).format(new Date(summary.state.resetsAt)),
            })}
          </p>
          {summary.local.pendingOperations > 0 && (
            <p role="status">
              {t("usage.pending", { count: summary.local.pendingOperations })}
            </p>
          )}
          {summary.local.failedSynchronizations > 0 && (
            <p role="alert" className="workspace-alert">
              {t("usage.sync_error")}
            </p>
          )}
          <label className="field">
            {t("usage.next_plan")}
            <select
              value={summary.nextPlan}
              disabled={busy}
              onChange={(e) => void selectPlan(e.target.value)}
            >
              {plans.map((plan) => (
                <option key={plan.key} value={plan.key}>
                  {plan.key}
                </option>
              ))}
            </select>
          </label>
          <p className="panel-note">{t("usage.plan_help")}</p>
          <h3>{t("usage.breakdown")}</h3>
          <ul className="usage-breakdown">
            {breakdown.map((row) => (
              <li key={row.feature + row.resourceId}>
                <div>
                  <strong>
                    {row.resourceId
                      ? (bots.find((bot) => bot.id === row.resourceId)?.name ??
                        t("usage.deleted_bot"))
                      : t("usage.knowledge")}
                  </strong>
                  <small>
                    {t("usage.operations", { count: row.operations })}
                  </small>
                </div>
                <span>
                  {credits(row.chargedMicrocredits)} {t("usage.credits")}
                </span>
              </li>
            ))}
          </ul>
        </>
      )}
    </section>
  );
}
