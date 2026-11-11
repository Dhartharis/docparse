import type { Language, Translate } from "../../i18n";
import { formatSourceDecimal } from "../../services/operational-data";
import type { OperationalRecord, Quantity } from "../../services/operational-data";

export default function RecordList({ records, language, t }: { records: OperationalRecord[]; language: Language; t: Translate }) {
  const date = (value: string) => new Intl.DateTimeFormat(language, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));
  const quantity = (value: Quantity, unit: string) => value.known && value.quantity !== null
    ? `${formatSourceDecimal(value.quantity, language)} ${unit}` : t("records.unknown");
  return <ul className="record-list">{records.map(({ offer, priceCurrent, availabilityCurrent }) => <li key={offer.externalId}>
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
      </li>)}</ul>;
}
