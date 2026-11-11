import { useRef, useState } from "react";
import type { FormEvent } from "react";
import type { Translate } from "../../i18n";
import type { ConnectionDefinition, SourceConnection } from "../../services/connections";

export default function ConnectionEditor({ existing, busy, t, onSave, onCancel }: {
  existing?: SourceConnection; busy: boolean; t: Translate;
  onSave: (requestId: string, definition: ConnectionDefinition) => Promise<void>; onCancel: () => void;
}) {
  const requestId = useRef(crypto.randomUUID());
  const [push, setPush] = useState(existing?.push ?? true);
  const [live, setLive] = useState(existing?.pullLive ?? false);
  const [scheduled, setScheduled] = useState(existing?.pullScheduled ?? false);
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    await onSave(requestId.current, {
      sourceBusinessId: existing?.sourceBusinessId ?? String(form.get("business")),
      baseUrl: existing?.baseUrl ?? (live || scheduled ? String(form.get("baseUrl")).trim() : null),
      push, pullLive: live, pullScheduled: scheduled, pushKeyDays: push ? Number(form.get("days") ?? 90) : 0,
      settings: {
        name: String(form.get("name")).trim(), priceMaxAgeSeconds: Number(form.get("price")),
        stockMaxAgeSeconds: Number(form.get("stock")), clockSkewSeconds: Number(form.get("skew")),
        syncIntervalSeconds: scheduled ? Number(form.get("interval")) : null,
      },
    });
  }
  return <form className="connection-editor" onSubmit={event => void submit(event)}>
    <h3>{t(existing ? "connections.edit" : "connections.add")}</h3>
    <fieldset disabled={busy}>
      <label className="field">{t("connections.name")}<input name="name" required maxLength={100} defaultValue={existing?.settings.name ?? ""} autoFocus /></label>
      {!existing && <>
        <label className="field">{t("connections.business")}<input name="business" required maxLength={128} pattern="[A-Za-z0-9][A-Za-z0-9._:-]*" autoComplete="off" /></label>
        <div className="connection-modes">
          <label><input type="checkbox" checked={push} onChange={e => setPush(e.target.checked)} />{t("connections.push")}</label>
          <label><input type="checkbox" checked={live} onChange={e => setLive(e.target.checked)} />{t("connections.live")}</label>
          <label><input type="checkbox" checked={scheduled} onChange={e => setScheduled(e.target.checked)} />{t("connections.scheduled")}</label>
        </div>
        {(live || scheduled) && <label className="field">{t("connections.endpoint")}<input name="baseUrl" type="url" required autoComplete="off" /></label>}
        {push && <label className="field">{t("connections.key_days")}<input name="days" type="number" required min={1} max={365} step={1} defaultValue={90} /></label>}
      </>}
      <div className="connection-settings">
        <label className="field">{t("connections.price_age")}<input name="price" type="number" required min={1} max={604800} step={1} defaultValue={existing?.settings.priceMaxAgeSeconds ?? 600} /></label>
        <label className="field">{t("connections.stock_age")}<input name="stock" type="number" required min={1} max={86400} step={1} defaultValue={existing?.settings.stockMaxAgeSeconds ?? 60} /></label>
        <label className="field">{t("connections.clock_skew")}<input name="skew" type="number" required min={0} max={300} step={1} defaultValue={existing?.settings.clockSkewSeconds ?? 0} /></label>
        {scheduled && <label className="field">{t("connections.interval")}<input name="interval" type="number" required min={60} max={86400} step={1} defaultValue={existing?.settings.syncIntervalSeconds ?? 300} /></label>}
      </div>
      <p className="panel-note">{t("connections.freshness_help")}</p>
      <div className="connection-actions">
        <button className="submit-button" type="submit" disabled={!push && !live && !scheduled}>{t("connections.save")}</button>
        <button className="secondary-button" type="button" onClick={onCancel}>{t("common.cancel")}</button>
      </div>
    </fieldset>
  </form>;
}
