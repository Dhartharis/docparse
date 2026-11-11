import { useEffect, useState } from "react";
import type { Language, MessageKey, Translate } from "../../i18n";
import { ApiError } from "../../services/api";
import { formatSourceDecimal, readOperationalRecords } from "../../services/operational-data";
import type { OperationalSnapshot, Quantity } from "../../services/operational-data";

export default function ReceivedRecords({ organizationId, connectionId, language, t }: { organizationId: string; connectionId: string; language: Language; t: Translate }) {
  const [snapshot, setSnapshot] = useState<OperationalSnapshot | null>(null);
  const [error, setError] = useState<MessageKey | null>(null);
  const [reload, setReload] = useState(0);
  const [loading, setLoading] = useState(true);
  useEffect(() => {
    const controller = new AbortController();
    void readOperationalRecords(organizationId, connectionId, controller.signal).then(value => {
      if (!controller.signal.aborted) setSnapshot(value);
    }).catch(failure => {
      if (!controller.signal.aborted) setError(failure instanceof ApiError ? `errors.${failure.code}` : "errors.server");
    }).finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [organizationId, connectionId, reload]);
  const date = (value: string) => new Intl.DateTimeFormat(language, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));
  const quantity = (value: Quantity, unit: string) => value.known && value.quantity !== null
    ? `${formatSourceDecimal(value.quantity, language)} ${unit}` : t("records.unknown");
  return <section className="received-records" aria-busy={loading}>
    <h4>{t("records.title")}</h4>
    <p className="panel-note">{t("records.help")}</p>
    <button type="button" className="secondary-button" disabled={loading} onClick={() => { setError(null); setSnapshot(null); setLoading(true); setReload(value => value + 1); }}>{t("connections.refresh")}</button>
    {loading && <p role="status">{t("workspace.loading")}</p>}
    {error && <p role="alert">{t(error)}</p>}
    {snapshot && <>
      <p>{t("records.count", { shown: snapshot.records.length, total: snapshot.total })}</p>
      {snapshot.records.length === 0 && <p>{t("records.empty")}</p>}
      <ul className="record-list">{snapshot.records.map(({ offer, priceCurrent, availabilityCurrent }) => <li key={offer.externalId}>
        <h5>{offer.name}</h5><p>{offer.presentation} · {offer.externalId}</p>
        {!offer.active && <p>{t("records.inactive")}</p>}
        <dl>
          <dt>{t("records.price")}</dt><dd>{offer.price.known && offer.price.amount !== null ? `${formatSourceDecimal(offer.price.amount, language)} ${offer.price.currency}` : t("records.unknown")}</dd>
          {offer.price.known && <><dt>{t("records.tax")}</dt><dd>{t(offer.price.taxTreatment === "inclusive" ? "records.tax_inclusive" : offer.price.taxTreatment === "exclusive" ? "records.tax_exclusive" : "records.tax_unspecified")}</dd></>}
          <dt>{t("records.available")}</dt><dd>{quantity(offer.availability.available, offer.unitCode)}</dd>
          <dt>{t("records.reserved")}</dt><dd>{quantity(offer.availability.reserved, offer.unitCode)}</dd>
          <dt>{t("records.price_status")}</dt><dd>{t(priceCurrent ? "records.current" : "records.not_current")}</dd>
          <dt>{t("records.stock_status")}</dt><dd>{t(availabilityCurrent ? "records.current" : "records.not_current")}</dd>
          {offer.price.observedAt && <><dt>{t("records.price_observed")}</dt><dd>{date(offer.price.observedAt)}</dd></>}
          <dt>{t("records.stock_observed")}</dt><dd>{date(offer.availability.observedAt)}</dd>
        </dl>
      </li>)}</ul>
    </>}
  </section>;
}
