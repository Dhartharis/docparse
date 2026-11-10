import { useCallback, useEffect, useState } from "react";
import { api, ApiError } from "../../services/api";
import type { KnowledgeBase } from "../../services/knowledge";
import type { Language, MessageKey, Translate } from "../../i18n";
import TrashAction from "../../components/TrashAction";
type Row = {
  id: string;
  name: string;
  purgeAt: string;
  knowledgeBaseId?: string;
  parentDeleted?: boolean;
  kind: "bot" | "base" | "document";
};
export default function TrashPanel({
  organizationId,
  t,
  language,
  onChanged,
}: {
  organizationId: string;
  t: Translate;
  language: Language;
  onChanged: () => void;
}) {
  const [rows, setRows] = useState<Row[]>([]);
  const [bases, setBases] = useState<KnowledgeBase[]>([]);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);
  const root = `/organizations/${organizationId}`;
  const load = useCallback(
    async (signal?: AbortSignal) => {
      try {
        const [bots, knowledge, documents, active] = await Promise.all([
          api<Row[]>(root + "/bots/trash", undefined, signal),
          api<Row[]>(root + "/knowledge-bases/trash", undefined, signal),
          api<Row[]>(
            root + "/knowledge-bases/trash/documents",
            undefined,
            signal,
          ),
          api<KnowledgeBase[]>(root + "/knowledge-bases/", undefined, signal),
        ]);
        if (!signal?.aborted) {
          setRows([
            ...bots.map((r) => ({ ...r, kind: "bot" as const })),
            ...knowledge.map((r) => ({ ...r, kind: "base" as const })),
            ...documents.map((r) => ({ ...r, kind: "document" as const })),
          ]);
          setBases(active);
          setError(null);
        }
      } catch (e) {
        if (!signal?.aborted)
          setError(
            e instanceof ApiError
              ? (`errors.${e.code}` as MessageKey)
              : "errors.server",
          );
      } finally {
        if (!signal?.aborted) setLoading(false);
      }
    },
    [root],
  );
  useEffect(() => {
    const controller = new AbortController();
    void Promise.resolve().then(() => { if (!controller.signal.aborted) return load(controller.signal); });
    return () => controller.abort();
  }, [load]);
  async function restore(row: Row) {
    if (busy) return;
    setBusy(true);
    setNotice(false);
    const path =
      row.kind === "bot"
        ? `/bots/${row.id}`
        : row.kind === "base"
          ? `/knowledge-bases/${row.id}`
          : `/knowledge-bases/${row.knowledgeBaseId}/documents/${row.id}`;
    try {
      await api(root + path + "/restore", {});
      setNotice(true);
      await load();
      onChanged();
    } catch (e) {
      setError(
        e instanceof ApiError
          ? (`errors.${e.code}` as MessageKey)
          : "errors.server",
      );
    } finally {
      setBusy(false);
    }
  }
  return (
    <section className="trash-panel">
      <p className="panel-note">{t("trash.description")}</p>
      {error && (
        <div role="alert" className="workspace-alert">
          <p>{t(error)}</p>
          <button type="button" disabled={busy} onClick={() => void load()}>
            {t("common.retry")}
          </button>
        </div>
      )}
      {notice && <p role="status">{t("trash.restored")}</p>}
      {loading ? (
        <p role="status">{t("workspace.loading")}</p>
      ) : rows.length === 0 ? (
        <p>{t("trash.empty")}</p>
      ) : (
        <ul>
          {rows.map((row) => (
            <li key={row.kind + row.id}>
              <div>
                <strong>{row.name}</strong>
                <small>
                  {t(`trash.kind_${row.kind}`)} ·{" "}
                  {t("trash.purge_at", {
                    date: new Intl.DateTimeFormat(language, {
                      dateStyle: "medium",
                    }).format(new Date(row.purgeAt)),
                  })}
                </small>
                {row.parentDeleted && (
                  <small>{t("trash.restore_parent")}</small>
                )}
              </div>
              <button
                type="button"
                className="text-button"
                disabled={busy || row.parentDeleted}
                onClick={() => void restore(row)}
              >
                {t("trash.restore")}
              </button>
            </li>
          ))}
        </ul>
      )}
      <details className="library-management">
        <summary>{t("trash.manage_bases")}</summary>
        <p className="panel-note">{t("trash.base_help")}</p>
        <ul>
          {bases.map((base) => (
            <li key={base.id}>
              <strong>{base.name}</strong>
              <TrashAction
                path={root + `/knowledge-bases/${base.id}`}
                name={base.name}
                label="trash.move_base"
                help="trash.base_help"
                t={t}
                disabled={busy}
                onBusyChange={setBusy}
                onMoved={() => {
                  void load();
                  onChanged();
                }}
              />
            </li>
          ))}
        </ul>
      </details>
    </section>
  );
}
