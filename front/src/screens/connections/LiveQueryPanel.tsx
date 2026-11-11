import { useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import type { Language, MessageKey, Translate } from "../../i18n";
import { ApiError } from "../../services/api";
import { querySource } from "../../services/operational-data";
import type { LiveResponse } from "../../services/operational-data";
import RecordList from "./RecordList";

export default function LiveQueryPanel({ organizationId, connectionId, language, t }: { organizationId: string; connectionId: string; language: Language; t: Translate }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);
  const [result, setResult] = useState<LiveResponse | null>(null);
  const [kind, setKind] = useState("text");
  const active = useRef<AbortController | null>(null);
  useEffect(() => () => active.current?.abort(), []);
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (active.current) return;
    const value = String(new FormData(event.currentTarget).get("value")).trim();
    const controller = new AbortController(); active.current = controller;
    setBusy(true); setError(null); setResult(null);
    try {
      const response = await querySource(organizationId, connectionId, { selectorKind: kind, values: [value], fields: ["catalog", "price", "availability"] }, controller.signal);
      if (!controller.signal.aborted) setResult(response);
    } catch (failure) {
      if (!controller.signal.aborted) setError(failure instanceof ApiError ? `errors.${failure.code}` : "errors.server");
    } finally { active.current = null; if (!controller.signal.aborted) setBusy(false); }
  }
  return <section className="received-records" aria-busy={busy}>
    <h4>{t("live.title")}</h4><p className="panel-note">{t("live.help")}</p>
    <form className="connection-editor" onSubmit={event => void submit(event)}><fieldset disabled={busy}>
      <label className="field workspace-picker">{t("live.selector")}<select value={kind} onChange={event => { setKind(event.target.value); setResult(null); }}>
        <option value="text">{t("live.text")}</option><option value="externalIds">{t("live.identifier")}</option><option value="skus">{t("live.sku")}</option>
      </select></label>
      <label className="field">{t("live.value")}<input name="value" required maxLength={kind === "text" ? 256 : 128} onChange={() => setResult(null)} /></label>
      <button type="submit" className="secondary-button">{t(busy ? "live.loading" : "live.submit")}</button>
    </fieldset></form>
    {error && <p role="alert">{t(error)}</p>}
    {result?.results.map(item => <div key={item.selectorValue}>
      <p role="status">{t(`live.${item.status}`)}</p>
      <RecordList records={item.records} language={language} t={t} />
    </div>)}
  </section>;
}
