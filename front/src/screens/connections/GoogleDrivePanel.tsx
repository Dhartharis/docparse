import { useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import type { Language, MessageKey, Translate } from "../../i18n";
import { ApiError } from "../../services/api";
import { authorizeGoogle, chooseGoogleFile, disconnectGoogle, googleAccounts, inspectGoogle, previewGoogle } from "../../services/google-drive";
import type { GoogleAccount, GoogleFile } from "../../services/google-drive";
import type { CatalogInspection, CatalogMapping, CatalogPreview } from "../../services/spreadsheet-catalog";
import CatalogMappingEditor from "./CatalogMappingEditor";
import CatalogPreviewResult from "./CatalogPreviewResult";

export default function GoogleDrivePanel({ organizationId, language, t, onBusyChange }: { organizationId: string; language: Language; t: Translate; onBusyChange: (busy: boolean) => void }) {
  const [data, setData] = useState<{ enabled: boolean; accounts: GoogleAccount[] } | null>(null);
  const [account, setAccount] = useState("");
  const [file, setFile] = useState<GoogleFile | null>(null);
  const [inspection, setInspection] = useState<CatalogInspection | null>(null);
  const [preview, setPreview] = useState<CatalogPreview | null>(null);
  const [sheet, setSheet] = useState(""); const [header, setHeader] = useState(1);
  const [busy, setBusy] = useState(true); const [confirm, setConfirm] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);
  const section = useRef<HTMLElement>(null); const controller = useRef<AbortController | null>(null);
  useEffect(() => { onBusyChange(busy); }, [busy, onBusyChange]);
  useEffect(() => {
    const active = new AbortController(); controller.current = active;
    void googleAccounts(organizationId, active.signal).then(result => { if (!active.signal.aborted) { setData(result); setAccount(result.accounts[0]?.id ?? ""); } }).catch(failure => {
      if (!active.signal.aborted) setError(failure instanceof ApiError ? `errors.${failure.code}` : "errors.server");
    }).finally(() => { if (!active.signal.aborted) { controller.current = null; setBusy(false); } });
    return () => { controller.current?.abort(); onBusyChange(false); };
  }, [organizationId, onBusyChange]);
  function resetFile() { setFile(null); setInspection(null); setPreview(null); setSheet(""); setHeader(1); }
  async function act(operation: (signal: AbortSignal) => Promise<void>) {
    if (controller.current) return;
    const active = new AbortController(); controller.current = active; setBusy(true); setError(null);
    try { await operation(active.signal); }
    catch (failure) { if (!active.signal.aborted) setError(failure instanceof ApiError ? `errors.${failure.code}` : "errors.server"); }
    finally { if (!active.signal.aborted) { controller.current = null; setBusy(false); } }
  }
  async function load(signal: AbortSignal) { const result = await googleAccounts(organizationId, signal); if (!signal.aborted) setData(result); return result; }
  async function choose(id: string, signal: AbortSignal) {
    const fileId = await chooseGoogleFile(organizationId, id, language, signal, section.current?.closest("dialog") ?? null);
    if (!fileId || signal.aborted) return;
    resetFile();
    const result = await inspectGoogle(organizationId, id, fileId, "", 1, signal);
    if (!signal.aborted) { setFile(result.file); setInspection(result.inspection); setSheet(result.inspection.sheet); if (result.inspection.headersValid === false) setError("errors.catalog_headers"); }
  }
  function connect() {
    if (busy || controller.current) return;
    const popup = window.open("about:blank", "_blank", "popup,width=560,height=720");
    if (!popup) { setError("google.popup"); return; }
    void act(async signal => { const id = await authorizeGoogle(organizationId, popup, signal); if (signal.aborted) return; setAccount(id); resetFile(); await load(signal); await choose(id, signal); });
  }
  async function inspect(event: FormEvent) {
    event.preventDefault(); if (!file) return; setPreview(null);
    await act(async signal => { const result = await inspectGoogle(organizationId, account, file.id, sheet, header, signal); if (!signal.aborted) { setFile(result.file); setInspection(result.inspection); if (result.inspection.headersValid === false) setError("errors.catalog_headers"); } });
  }
  async function map(mapping: CatalogMapping) {
    if (!file) return; setPreview(null);
    await act(async signal => { const result = await previewGoogle(organizationId, account, file.id, sheet, mapping, signal); if (!signal.aborted) { setFile(result.file); setPreview(result.preview); } });
  }
  const selected = data?.accounts.find(item => item.id === account);
  return <section className="connection-editor" ref={section} aria-busy={busy}>
    <h3>{t("google.title")}</h3><p className="panel-note">{t("google.help")}</p><p className="panel-note">{t("google.preview_help")}</p>
    {error && <p role="alert">{t(error)}</p>}
    {data?.enabled === false && <p>{t("google.disabled")}</p>}
    {!data && busy && <p role="status">{t("workspace.loading")}</p>}
    {!data && !busy && <button type="button" className="secondary-button" onClick={() => void act(async signal => { const result = await load(signal); if (!signal.aborted) setAccount(result.accounts[0]?.id ?? ""); })}>{t("connections.refresh")}</button>}
    {data && <><fieldset disabled={busy || confirm}>
      <button type="button" className="secondary-button" disabled={!data.enabled} onClick={connect}>{t("google.connect")}</button>
      {data.accounts.length > 0 && <label className="field workspace-picker">{t("google.account")}<select value={account} onChange={event => { setAccount(event.target.value); resetFile(); setError(null); }}>{data.accounts.map(item => <option key={item.id} value={item.id}>{t("google.connected", { date: new Intl.DateTimeFormat(language, { dateStyle: "medium", timeStyle: "short" }).format(new Date(item.createdAt)) })}</option>)}</select></label>}
      {selected?.needsReconnect && <p role="status">{t("google.reconnect")}</p>}
      {selected && <div className="connection-actions"><button type="button" className="secondary-button" disabled={!data.enabled || selected.needsReconnect} onClick={() => void act(signal => choose(account, signal))}>{t("google.choose")}</button><button type="button" className="secondary-button" onClick={() => setConfirm(true)}>{t("google.disconnect")}</button></div>}
    </fieldset>
    {confirm && <div role="group" aria-label={t("google.disconnect")}><p>{t("google.disconnect_help")}</p><div className="connection-actions"><button type="button" disabled={busy} onClick={() => void act(async signal => { await disconnectGoogle(organizationId, account, signal); if (signal.aborted) return; setConfirm(false); setAccount(""); resetFile(); const remaining = await load(signal); if (!signal.aborted) setAccount(remaining.accounts[0]?.id ?? ""); })}>{t("google.disconnect")}</button><button type="button" className="secondary-button" disabled={busy} onClick={() => setConfirm(false)}>{t("common.cancel")}</button></div></div>}
    {file && <><h4 className="google-drive-file-name">{file.name}</h4><p className="panel-note">{t("google.formula_help")}</p><form onSubmit={event => void inspect(event)}><fieldset disabled={busy || confirm}>
      {inspection && <label className="field workspace-picker">{t("catalog.sheet")}<select value={sheet} onChange={event => { setSheet(event.target.value); setPreview(null); }}>{inspection.sheets.map(value => <option key={value}>{value}</option>)}</select></label>}
      <label className="field">{t("catalog.header_row")}<input type="number" min={1} max={50} step={1} required value={header} onChange={event => { setHeader(Number(event.target.value)); setInspection(null); setPreview(null); }} /></label>
      <button type="submit" className="secondary-button">{t("catalog.inspect")}</button>
    </fieldset></form></>}
    {file && inspection && inspection.headersValid !== false && inspection.sheet === sheet && <CatalogMappingEditor key={JSON.stringify([file.id, account, sheet, header, inspection.headers])} inspection={inspection} headerRow={header} busy={busy || confirm} t={t} onPreview={map} showCachedFormulas={file.mimeType === "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"} />}
    {preview && <CatalogPreviewResult preview={preview} language={language} t={t} />}
    </>}
  </section>;
}
