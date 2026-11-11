import { useEffect, useRef, useState } from "react";
import type { Language, MessageKey, Translate } from "../../i18n";
import { ApiError } from "../../services/api";
import { readSyncStatus, restartSync } from "../../services/operational-data";
import type { SourceConnection } from "../../services/connections";
import type { SyncStatus } from "../../services/operational-data";

export default function SyncPanel({ organizationId, connection, language, t }: { organizationId: string; connection: SourceConnection; language: Language; t: Translate }) {
  const [status, setStatus] = useState<SyncStatus | null>(null);
  const [error, setError] = useState<MessageKey | null>(null);
  const [confirm, setConfirm] = useState(false);
  const [busy, setBusy] = useState(false);
  const [revision, setRevision] = useState(0);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const mutation = useRef<AbortController | null>(null);
  const heading = useRef<HTMLHeadingElement | null>(null);
  const focused = useRef(false);
  useEffect(() => {
    if (!status || focused.current) return;
    focused.current = true;
    heading.current?.focus({ preventScroll: true });
    heading.current?.scrollIntoView({ block: "start" });
  }, [status]);
  useEffect(() => {
    const controller = new AbortController();
    async function load() {
      try {
        const result = await readSyncStatus(organizationId, connection.id, controller.signal);
        if (!controller.signal.aborted) { setStatus(result); setError(null); }
      } catch (failure) { if (!controller.signal.aborted) setError(failure instanceof ApiError ? `errors.${failure.code}` : "errors.server"); }
      finally { if (!controller.signal.aborted) timer.current = setTimeout(() => void load(), 5000); }
    }
    void load();
    return () => { if (timer.current) clearTimeout(timer.current); controller.abort(); mutation.current?.abort(); };
  }, [organizationId, connection.id, revision]);
  async function restart() {
    if (mutation.current) return;
    const controller = new AbortController(); mutation.current = controller;
    setBusy(true); setError(null);
    try { await restartSync(organizationId, connection, controller.signal); if (!controller.signal.aborted) { setConfirm(false); setRevision(value => value + 1); } }
    catch (failure) { if (!controller.signal.aborted) setError(failure instanceof ApiError ? `errors.${failure.code}` : "errors.server"); }
    finally { mutation.current = null; if (!controller.signal.aborted) setBusy(false); }
  }
  const date = (value: string | null) => value ? new Intl.DateTimeFormat(language, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value)) : t("source_sync.not_yet");
  const sourceErrors = ["connection_not_ready", "provider_credentials", "provider_scope", "provider_rate", "provider_response", "provider_unavailable"] as const;
  const sourceError = sourceErrors.find(value => value === status?.errorCode);
  return <section className="received-records" aria-busy={busy}>
    <h4 ref={heading} tabIndex={-1}>{t("source_sync.title")}</h4><p className="panel-note">{t("source_sync.help")}</p>
    {error && <p role="alert">{t(error)}</p>}
    {!status && !error && <p role="status">{t("workspace.loading")}</p>}
    {status && <>
      <p role="status">{t(`source_sync.${status.state}`)}</p>
      <p>{t(status.snapshotCompleted ? "source_sync.snapshot_complete" : "source_sync.snapshot_pending")}</p>
      <p>{t("source_sync.last_page", { date: date(status.lastSuccessAt) })}</p>
      <p>{t("source_sync.last_batch", { date: date(status.lastCompletedAt) })}</p>
      {status.state === "waiting" && <p>{t("source_sync.next", { date: date(status.dueAt) })}</p>}
      {status.attempts > 0 && <p>{t("source_sync.attempts", { count: status.attempts })}</p>}
      {status.errorCode && <p>{t(sourceError ? `errors.${sourceError}` : status.errorCode === "cursor_expired" ? "source_sync.cursor_expired" : "source_sync.error")}</p>}
    </>}
    {!confirm ? <button type="button" className="secondary-button" onClick={() => setConfirm(true)}>{t("source_sync.restart")}</button> : <div>
      <p>{t("source_sync.restart_help")}</p><div className="connection-actions">
        <button type="button" className="secondary-button" disabled={busy} onClick={() => void restart()}>{t("connections.confirm")}</button>
        <button type="button" className="secondary-button" disabled={busy} onClick={() => setConfirm(false)}>{t("common.cancel")}</button>
      </div>
    </div>}
  </section>;
}
