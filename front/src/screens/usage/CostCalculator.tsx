import { useState } from "react";
import type { FormEvent } from "react";
import type { Language, MessageKey, Translate } from "../../i18n";
import { api } from "../../services/api";

type Scenario = {
  name: string;
  queries: number;
  inputTokensPerQuery: number;
  outputTokensPerQuery: number;
  queryEmbeddingTokens: number;
  documentEmbeddingTokens: number;
  retrievalUsdPerQuery: number;
  storageUsd: number;
  egressUsd: number;
  processingUsd: number;
};
type Estimate = {
  name: string;
  queryCostUsd: number;
  ingestionCostUsd: number;
  totalCostUsd: number;
  costBudgetUsd: number;
  costFraction: number | null;
  maximumQueriesWithinBudget: number | null;
  withinBudget: boolean;
  withinAbsoluteMargin: boolean;
};
const fields = [
  "queries",
  "inputTokensPerQuery",
  "outputTokensPerQuery",
  "queryEmbeddingTokens",
  "documentEmbeddingTokens",
  "retrievalUsdPerQuery",
  "storageUsd",
  "egressUsd",
  "processingUsd",
] as const;
const rateFields = [
  "inputUsdPerMillion",
  "outputUsdPerMillion",
  "embeddingUsdPerMillion",
] as const;
export default function CostCalculator({
  organizationId,
  t,
  language,
}: {
  organizationId: string;
  t: Translate;
  language: Language;
}) {
  const [price, setPrice] = useState(0);
  const [freeBudget, setFreeBudget] = useState(0);
  const [rates, setRates] = useState({
    inputUsdPerMillion: 0,
    outputUsdPerMillion: 0,
    embeddingUsdPerMillion: 0,
  });
  const [scenarios, setScenarios] = useState<Scenario[]>(
    ["small", "normal", "heavy"].map((name) => ({
      name,
      queries: 0,
      inputTokensPerQuery: 0,
      outputTokensPerQuery: 0,
      queryEmbeddingTokens: 0,
      documentEmbeddingTokens: 0,
      retrievalUsdPerQuery: 0,
      storageUsd: 0,
      egressUsd: 0,
      processingUsd: 0,
    })),
  );
  const [selected, setSelected] = useState(0);
  const [estimates, setEstimates] = useState<Estimate[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(false);
  const scenario = scenarios[selected];
  const money = (value: number) =>
    new Intl.NumberFormat(language, {
      style: "currency",
      currency: "USD",
      maximumFractionDigits: 6,
    }).format(value);
  async function calculate(event: FormEvent) {
    event.preventDefault();
    if (busy) return;
    setBusy(true);
    setError(false);
    setEstimates([]);
    try {
      setEstimates(
        await api<Estimate[]>(
          `/organizations/${organizationId}/usage/estimate`,
          {
            monthlyPriceUsd: price,
            freeBudgetUsd: freeBudget,
            targetCostFraction: 0.4,
            scenarios: scenarios.map((s) => ({ ...s, ...rates })),
          },
        ),
      );
    } catch {
      setError(true);
    } finally {
      setBusy(false);
    }
  }
  return (
    <section className="cost-calculator">
      <p className="panel-note">{t("economics.description")}</p>
      <form onSubmit={calculate} onChange={() => setEstimates([])}>
        <fieldset disabled={busy}>
          <div className="cost-fields">
            <label className="field">
              {t("economics.price")}
              <input
                type="number"
                min="0"
                max="1000000"
                step="any"
                required
                value={price}
                onChange={(e) => setPrice(Number(e.target.value))}
              />
            </label>
            <label className="field">
              {t("economics.free_budget")}
              <input
                type="number"
                min="0"
                max="1000000"
                step="any"
                required
                value={freeBudget}
                onChange={(e) => setFreeBudget(Number(e.target.value))}
              />
            </label>
            {rateFields.map((key) => (
              <label className="field" key={key}>
                {t(
                  `economics.${key.replace(/[A-Z]/g, (c) => "_" + c.toLowerCase())}` as MessageKey,
                )}
                <input
                  type="number"
                  min="0"
                  max="1000000000"
                  step="any"
                  required
                  value={rates[key]}
                  onChange={(e) =>
                    setRates({ ...rates, [key]: Number(e.target.value) })
                  }
                />
              </label>
            ))}
          </div>
          <label className="field">
            {t("economics.scenario")}
            <select
              value={selected}
              onChange={(e) => setSelected(Number(e.target.value))}
            >
              {scenarios.map((s, i) => (
                <option key={s.name} value={i}>
                  {t(`economics.${s.name}` as "economics.small")}
                </option>
              ))}
            </select>
          </label>
          <div className="cost-fields">
            {fields.map((key) => (
              <label className="field" key={key}>
                {t(
                  `economics.${key.replace(/[A-Z]/g, (c) => "_" + c.toLowerCase())}` as MessageKey,
                )}
                <input
                  type="number"
                  min="0"
                  max="1000000000"
                  step={key === "queries" ? "1" : "any"}
                  required
                  value={scenario[key]}
                  onChange={(e) =>
                    setScenarios((previous) =>
                      previous.map((s, i) =>
                        i === selected
                          ? { ...s, [key]: Number(e.target.value) }
                          : s,
                      ),
                    )
                  }
                />
              </label>
            ))}
          </div>
          <button className="secondary-button" disabled={busy}>
            {t(busy ? "auth.working" : "economics.calculate")}
          </button>
        </fieldset>
      </form>
      {error && (
        <p role="alert" className="workspace-alert">
          {t("errors.server")}
        </p>
      )}
      {estimates.length > 0 && (
        <div aria-live="polite" className="cost-results">
          {estimates.map((result) => (
            <article key={result.name}>
              <h3>{t(`economics.${result.name}` as "economics.small")}</h3>
              <dl>
                {(
                  [
                    ["per_query", money(result.queryCostUsd)],
                    ["ingestion", money(result.ingestionCostUsd)],
                    ["total", money(result.totalCostUsd)],
                    ["budget", money(result.costBudgetUsd)],
                    [
                      "maximum",
                      result.maximumQueriesWithinBudget === null
                        ? t("economics.no_variable_cost")
                        : String(result.maximumQueriesWithinBudget),
                    ],
                  ] as const
                ).map(([key, value]) => (
                  <div key={key}>
                    <dt>
                      {t(
                        `economics.${key.replace(/[A-Z]/g, (c) => "_" + c.toLowerCase())}` as MessageKey,
                      )}
                    </dt>
                    <dd>{value}</dd>
                  </div>
                ))}
              </dl>
              <p>
                {t(
                  result.withinBudget
                    ? "economics.within"
                    : "economics.exceeds",
                )}
              </p>
              {!result.withinAbsoluteMargin && (
                <p className="workspace-alert">
                  {t("economics.margin_warning")}
                </p>
              )}
            </article>
          ))}
        </div>
      )}
    </section>
  );
}
