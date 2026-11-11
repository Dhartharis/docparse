import { useCallback, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import type { Language, MessageKey, Translate } from "../../i18n";
import { ApiError } from "../../services/api";
import { createConnection, disconnectConnection, listConnections, rotatePushKey, savePullKey, updateConnection } from "../../services/connections";
import type { ConnectionDefinition, ConnectionList, SourceConnection } from "../../services/connections";
import ConnectionEditor from "./ConnectionEditor";

type Issued = { connectionId: string; keyId: string; secret: string };
type Editor = { kind: "new" } | { kind: "settings" | "pull"; connection: SourceConnection };
type Confirmation = { kind: "rotate" | "disconnect"; connection: SourceConnection };

export default function ConnectionsPanel({ organizationId, language, t, onBusyChange }: { organizationId: string; language: Language; t: Translate; onBusyChange: (busy: boolean) => void }) {
  const [data, setData] = useState<ConnectionList | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);
  const [notice, setNotice] = useState<MessageKey | null>(null);
  const [editor, setEditor] = useState<Editor | null>(null);
  const [confirmation, setConfirmation] = useState<Confirmation | null>(null);
  const [issued, setIssued] = useState<Issued | null>(null);
  const [copied, setCopied] = useState(false);
  const controller = useRef<AbortController | null>(null);
  const secretField = useRef<HTMLTextAreaElement | null>(null);
  const inFlight = useRef(false);
  const issuedKeyId = issued?.keyId;
  useEffect(() => {
    if (!issuedKeyId) return;
    secretField.current?.focus();
    secretField.current?.select();
    secretField.current?.closest("dialog")?.scrollTo({ top: 0 });
  }, [issuedKeyId]);
  const load = useCallback(async (signal: AbortSignal) => {
    const value = await listConnections(organizationId, signal);
    if (!signal.aborted) {
      setData(value);
      setIssued(previous => previous && value.connections.some(c => c.id === previous.connectionId && !c.disconnectedAt && c.pushKey?.keyId === previous.keyId) ? previous : null);
    }
  }, [organizationId]);
  useEffect(() => {
    const active = new AbortController(); controller.current = active;
    void Promise.resolve().then(async () => {
      try { if (!active.signal.aborted) await load(active.signal); }
      catch (failure) { if (!active.signal.aborted) setError(failure instanceof ApiError ? `errors.${failure.code}` : "errors.server"); }
    });
    return () => { active.abort(); onBusyChange(false); };
  }, [load, onBusyChange]);
  async function act(operation: (signal: AbortSignal) => Promise<void>) {
    if (inFlight.current || !controller.current) return;
    const signal = controller.current.signal;
    inFlight.current = true; setBusy(true); setError(null); setNotice(null); setCopied(false);
    onBusyChange(true);
    try { await operation(signal); }
    catch (failure) { if (!signal.aborted) setError(failure instanceof ApiError ? `errors.${failure.code}` : "errors.server"); }
    finally { inFlight.current = false; if (!signal.aborted) { setBusy(false); onBusyChange(false); } }
  }
  async function save(requestId: string, definition: ConnectionDefinition) {
    await act(async signal => {
      if (editor?.kind === "settings") await updateConnection(organizationId, editor.connection, definition.settings, signal);
      else {
        const value = await createConnection(organizationId, requestId, definition, signal);
        if (signal.aborted) return;
        if (value.pushSecretHex && value.connection.pushKey) setIssued({ connectionId: value.connection.id, keyId: value.connection.pushKey.keyId, secret: value.pushSecretHex });
        else if (value.connection.push) setNotice(value.connection.disconnectedAt ? "connections.state_disconnected" : "connections.secret_not_repeated");
      }
      if (!signal.aborted) setEditor(null);
      await load(signal);
    });
  }
  async function confirm(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const days = Number(new FormData(event.currentTarget).get("days"));
    if (!confirmation) return;
    const choice = confirmation;
    await act(async signal => {
      if (choice.kind === "disconnect") {
        await disconnectConnection(organizationId, choice.connection, signal);
        if (!signal.aborted) setIssued(previous => previous?.connectionId === choice.connection.id ? null : previous);
      } else {
        const value = await rotatePushKey(organizationId, choice.connection, days, signal);
        if (!signal.aborted && value.connection.pushKey) setIssued({ connectionId: value.connection.id, keyId: value.connection.pushKey.keyId, secret: value.secretHex });
      }
      if (!signal.aborted) setConfirmation(null);
      await load(signal);
    });
  }
  async function savePull(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (editor?.kind !== "pull") return;
    const fields = new FormData(event.currentTarget);
    const connection = editor.connection;
    await act(async signal => {
      await savePullKey(organizationId, connection, String(fields.get("keyId")).trim(), String(fields.get("secret")).trim(), signal);
      if (!signal.aborted) { setEditor(null); setNotice("connections.pull_saved"); }
      await load(signal);
    });
  }
  async function copySecret() {
    if (!issued) return;
    try { await navigator.clipboard.writeText(issued.secret); setCopied(true); }
    catch { setError("channels.copy_failed"); }
  }
  return <section className="connections-panel" aria-busy={busy}>
    <p className="panel-note">{t("connections.intro")}</p>
    {error && <p className="workspace-alert" role="alert">{t(error)}</p>}
    {notice && <p role="status">{t(notice)}</p>}
    {!data && !error && <p role="status">{t("workspace.loading")}</p>}
    {issued && <div className="connection-secret">
      <h3>{t("connections.new_key")}</h3><p className="panel-note">{t("connections.secret_once")}</p>
      <label className="field">{t("connections.key_id")}<input readOnly value={issued.keyId} /></label>
      <label className="field">{t("connections.secret")}<textarea ref={secretField} readOnly rows={3} value={issued.secret} onFocus={e => e.target.select()} /></label>
      <div className="connection-actions">
        <button type="button" className="secondary-button" onClick={() => void copySecret()}>{t(copied ? "channels.copied" : "channels.copy")}</button>
        <button type="button" className="secondary-button" onClick={() => setIssued(null)}>{t("connections.hide_key")}</button>
      </div>
    </div>}
    <div className="connection-actions">
      <button type="button" className="secondary-button" disabled={busy || Boolean(editor || confirmation)} onClick={() => { setEditor({ kind: "new" }); setError(null); }}>{t("connections.add")}</button>
      <button type="button" className="secondary-button" disabled={busy} onClick={() => void act(async signal => { setEditor(null); setConfirmation(null); await load(signal); })}>{t("connections.refresh")}</button>
    </div>
    {editor && editor.kind !== "pull" && <ConnectionEditor key={editor.kind === "new" ? "new" : editor.connection.id} existing={editor.kind === "settings" ? editor.connection : undefined} busy={busy} t={t} onSave={save} onCancel={() => setEditor(null)} />}
    {editor?.kind === "new" && !data?.providerOriginsConfigured && <p className="panel-note">{t("connections.origin_setup")}</p>}
    {editor?.kind === "pull" && <form className="connection-editor" onSubmit={event => void savePull(event)}>
      <h3>{t("connections.pull_key")}</h3><p className="panel-note">{t("connections.pull_help")}</p>
      <fieldset disabled={busy}>
        <label className="field">{t("connections.key_id")}<input name="keyId" defaultValue={editor.connection.pullKeyId ?? ""} required maxLength={128} pattern="[A-Za-z0-9._-]+" autoComplete="off" autoFocus /></label>
        <label className="field">{t("connections.secret")}<input name="secret" type="password" required minLength={64} maxLength={64} pattern="[A-Fa-f0-9]{64}" autoComplete="off" /></label>
        <div className="connection-actions"><button type="submit" className="submit-button">{t("connections.save")}</button><button type="button" className="secondary-button" onClick={() => setEditor(null)}>{t("common.cancel")}</button></div>
      </fieldset>
    </form>}
    {confirmation && <form className="connection-editor" onSubmit={event => void confirm(event)}>
      <h3>{t(confirmation.kind === "rotate" ? "connections.rotate_confirm" : "connections.disconnect_confirm", { name: confirmation.connection.settings.name })}</h3>
      <p>{t(confirmation.kind === "rotate" ? "connections.rotate_help" : "connections.disconnect_help")}</p>
      <fieldset disabled={busy}>
        {confirmation.kind === "rotate" && <label className="field">{t("connections.key_days")}<input name="days" type="number" min={1} max={365} step={1} required defaultValue={90} /></label>}
        <div className="connection-actions"><button type="submit" className="submit-button">{t("connections.confirm")}</button><button type="button" className="secondary-button" onClick={() => setConfirmation(null)}>{t("common.cancel")}</button></div>
      </fieldset>
    </form>}
    {data && data.connections.length === 0 && <p>{t("connections.empty")}</p>}
    <ul className="connection-list">{data?.connections.map(connection => <li key={connection.id}>
      <div className="connection-heading"><h3>{connection.settings.name}</h3><span>{t(`connections.state_${connection.state}`)}</span></div>
      <p className="panel-note">{t("connections.source", { business: connection.sourceBusinessId })}</p>
      {connection.baseUrl && <p className="connection-url">{connection.baseUrl}</p>}
      <ul className="connection-modes">{connection.push && <li>{t("connections.push")}</li>}{connection.pullLive && <li>{t("connections.live")}</li>}{connection.pullScheduled && <li>{t("connections.scheduled")}</li>}</ul>
      {connection.pushKey && <p className="panel-note">{t("connections.expires", { date: new Intl.DateTimeFormat(language, { dateStyle: "medium", timeStyle: "short" }).format(new Date(connection.pushKey.expiresAt)) })}</p>}
      {!connection.disconnectedAt && <div className="connection-actions">
        <button type="button" className="text-button" disabled={busy || Boolean(editor || confirmation)} onClick={() => setEditor({ kind: "settings", connection })}>{t("connections.edit")}</button>
        {connection.push && <button type="button" className="text-button" disabled={busy || Boolean(editor || confirmation)} onClick={() => setConfirmation({ kind: "rotate", connection })}>{t("connections.rotate")}</button>}
        {(connection.pullLive || connection.pullScheduled) && <button type="button" className="text-button" disabled={busy || Boolean(editor || confirmation)} onClick={() => setEditor({ kind: "pull", connection })}>{t("connections.pull_key")}</button>}
        <button type="button" className="text-button" disabled={busy || Boolean(editor || confirmation)} onClick={() => setConfirmation({ kind: "disconnect", connection })}>{t("connections.disconnect")}</button>
      </div>}
    </li>)}</ul>
  </section>;
}
