import RecordList from "./RecordList";
import { useEffect, useState } from "react";
import type { Language, MessageKey, Translate } from "../../i18n";
import { ApiError } from "../../services/api";
import { readOperationalRecords } from "../../services/operational-data";
import type { OperationalSnapshot } from "../../services/operational-data";

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
  return <section className="received-records" aria-busy={loading}>
    <h4>{t("records.title")}</h4>
    <p className="panel-note">{t("records.help")}</p>
    <button type="button" className="secondary-button" disabled={loading} onClick={() => { setError(null); setSnapshot(null); setLoading(true); setReload(value => value + 1); }}>{t("connections.refresh")}</button>
    {loading && <p role="status">{t("workspace.loading")}</p>}
    {error && <p role="alert">{t(error)}</p>}
    {snapshot && <>
      <p>{t("records.count", { shown: snapshot.records.length, total: snapshot.total })}</p>
      {snapshot.records.length === 0 && <p>{t("records.empty")}</p>}
      <RecordList records={snapshot.records} language={language} t={t} />
    </>}
  </section>;
}
