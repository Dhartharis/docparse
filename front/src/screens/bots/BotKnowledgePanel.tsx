import { useCallback, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { knowledge } from "../../services/knowledge";
import type { KnowledgeBase } from "../../services/knowledge";
import { api, ApiError } from "../../services/api";
import { bots } from "../../services/bots";
import type { Language, MessageKey, Translate } from "../../i18n";
import type { SyncState } from "./BotSyncStatus";
import DocumentsPanel from "../knowledge/DocumentsPanel";
import Modal from "../../components/Modal";

export default function BotKnowledgePanel({
  organizationId,
  botId,
  botName,
  t,
  language,
  disabled,
  onBusyChange,
  onSyncStatusChange,
}: {
  organizationId: string;
  botId: string;
  botName: string;
  t: Translate;
  language: Language;
  disabled: boolean;
  onBusyChange: (value: boolean) => void;
  onSyncStatusChange: (value: SyncState) => void;
}) {
  const [bases, setBases] = useState<KnowledgeBase[]>([]);
  const [linked, setLinked] = useState<string[]>([]);
  const [selected, setSelected] = useState("");
  const [checked, setChecked] = useState<string[]>([]);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);
  const [status, setStatus] = useState<SyncState>("saving");
  const [creating, setCreating] = useState(false);
  const [editing, setEditing] = useState("");
  const [renameDraft, setRenameDraft] = useState("");
  const [removing, setRemoving] = useState(false);
  const name = useRef<HTMLInputElement>(null);
  const lifecycle = useRef<AbortController | null>(null);
  const retry = useRef<(() => Promise<void>) | null>(null);
  const renameTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(() => onSyncStatusChange(status), [status, onSyncStatusChange]);
  const load = useCallback(
    async (signal: AbortSignal) => {
      setStatus("saving");
      try {
        const [available, current] = await Promise.all([
          knowledge.list(organizationId, signal),
          api<{ knowledgeBaseIds: string[] }>(
            `/organizations/${organizationId}/bots/${botId}/knowledge-bases`,
            undefined,
            signal,
          ),
        ]);
        if (!signal.aborted) {
          setBases(available);
          setLinked(current.knowledgeBaseIds);
          setSelected("");
          setError(null);
          setStatus("synced");
        }
      } catch (failure) {
        if (!signal.aborted) {
          setError(
            failure instanceof ApiError
              ? (`errors.${failure.code}` as MessageKey)
              : "errors.server",
          );
          setStatus("error");
        }
      } finally {
        if (!signal.aborted) setLoading(false);
      }
    },
    [organizationId, botId],
  );
  useEffect(() => {
    const value = new AbortController();
    lifecycle.current = value;
    void Promise.resolve().then(() => {
      if (!value.signal.aborted) return load(value.signal);
    });
    return () => {
      value.abort();
      if (renameTimer.current) clearTimeout(renameTimer.current);
    };
  }, [load]);
  function changeBusy(value: boolean) {
    setBusy(value);
    onBusyChange(value);
  }
  async function run(
    action: () => Promise<void>,
    recovery: (() => Promise<void>) | null = action,
  ) {
    if (busy || disabled) return;
    const signal = lifecycle.current!.signal;
    retry.current = recovery;
    changeBusy(true);
    setError(null);
    setStatus("saving");
    try {
      await action();
      if (!signal.aborted) {
        setStatus("synced");
        retry.current = null;
      }
    } catch (failure) {
      if (!signal.aborted) {
        setError(
          failure instanceof ApiError
            ? (`errors.${failure.code}` as MessageKey)
            : "errors.server",
        );
        setStatus("error");
      }
    } finally {
      if (!signal.aborted) changeBusy(false);
    }
  }
  async function persistLinks(ids: string[]) {
    const signal = lifecycle.current!.signal;
    await bots.link(organizationId, botId, ids, signal);
    if (!signal.aborted) {
      setLinked(ids);
      setChecked([]);
      setSelected((current) => (ids.includes(current) ? current : ""));
    }
  }
  function attach(id: string) {
    if (id) void run(() => persistLinks([...linked, id]));
  }
  async function create(event: FormEvent) {
    event.preventDefault();
    const value = name.current?.value.trim();
    if (!value) return;
    await run(async () => {
      const signal = lifecycle.current!.signal;
      const base = await knowledge.create(organizationId, value, signal);
      if (signal.aborted) return;
      setBases((previous) => [...previous, base]);
      setCreating(false);
      const ids = [...linked, base.id];
      retry.current = () => persistLinks(ids);
      await persistLinks(ids);
      if (!signal.aborted) setSelected(base.id);
    }, null);
  }
  async function rename(id: string, value: string) {
    if (renameTimer.current) clearTimeout(renameTimer.current);
    if (!value.trim()) {
      setStatus("error");
      setError("errors.validation");
      return;
    }
    await run(async () => {
      const signal = lifecycle.current!.signal;
      await knowledge.rename(organizationId, id, value.trim(), signal);
      if (!signal.aborted) {
        setBases((previous) =>
          previous.map((base) =>
            base.id === id ? { ...base, name: value.trim() } : base,
          ),
        );
        setEditing("");
        requestAnimationFrame(() =>
          document.getElementById(`rename-${id}`)?.focus(),
        );
      }
    });
  }
  function editName(id: string, value: string) {
    setRenameDraft(value);
    setError(null);
    setStatus("pending");
    onBusyChange(true);
    if (renameTimer.current) clearTimeout(renameTimer.current);
    renameTimer.current = setTimeout(() => void rename(id, value), 600);
  }
  function cancelRename() {
    if (renameTimer.current) clearTimeout(renameTimer.current);
    setEditing("");
    setError(null);
    setStatus("synced");
    onBusyChange(false);
  }
  const active = bases.find(
    (base) => base.id === selected && linked.includes(base.id),
  );
  const available = bases.filter((base) => !linked.includes(base.id));
  if (loading) return <p>{t("knowledge.loading")}</p>;
  return (
    <section className="bot-knowledge" aria-label={t("assistant.knowledge")}>
      <div className="knowledge-actions compact-knowledge-actions">
        <button
          type="button"
          className="text-button"
          disabled={busy || disabled || !!editing}
          onClick={() => setCreating((value) => !value)}
        >
          <span aria-hidden="true">+</span> {t("knowledge.new")}
        </button>
        {
          <button
            type="button"
            className="text-button unlink-bases"
            disabled={busy || disabled || !!editing || checked.length === 0}
            onClick={() => setRemoving(true)}
          >
            {t("knowledge.unlink_selected", { count: checked.length })}
          </button>
        }
        {available.length > 0 && (
          <label className="base-attach">
            <span className="sr-only">{t("knowledge.attach_existing")}</span>
            <select
              value=""
              disabled={busy || disabled || !!editing}
              onChange={(event) => attach(event.target.value)}
            >
              <option value="">{t("knowledge.attach_existing")}</option>
              {available.map((base) => (
                <option key={base.id} value={base.id}>
                  {base.name}
                </option>
              ))}
            </select>
          </label>
        )}
      </div>
      {creating && (
        <form
          className="inline-base-create"
          onSubmit={(event) => void create(event)}
        >
          <div className="field">
            <label htmlFor="card-base-name">{t("knowledge.name")}</label>
            <input
              id="card-base-name"
              ref={name}
              defaultValue={botName}
              required
              maxLength={200}
              disabled={busy || disabled}
              autoFocus
            />
          </div>
          <button className="secondary-button" disabled={busy || disabled}>
            {t("knowledge.create")}
          </button>
          <button
            type="button"
            className="text-button"
            disabled={busy}
            onClick={() => setCreating(false)}
          >
            {t("common.cancel")}
          </button>
        </form>
      )}
      <ul className="base-list" aria-label={t("knowledge.bases")}>
        {bases
          .filter((base) => linked.includes(base.id))
          .map((base) => (
            <li key={base.id}>
              <div className="base-row">
                <input
                  type="checkbox"
                  aria-label={t("knowledge.select_base", { name: base.name })}
                  checked={checked.includes(base.id)}
                  disabled={busy || disabled || !!editing}
                  onChange={() =>
                    setChecked((previous) =>
                      previous.includes(base.id)
                        ? previous.filter((id) => id !== base.id)
                        : [...previous, base.id],
                    )
                  }
                />
                {editing === base.id ? (
                  <input
                    className="base-rename-input"
                    aria-label={t("knowledge.name")}
                    value={renameDraft}
                    maxLength={200}
                    autoFocus
                    disabled={busy}
                    onChange={(event) => editName(base.id, event.target.value)}
                    onKeyDown={(event) => {
                      if (event.key === "Enter") {
                        event.preventDefault();
                        void rename(base.id, renameDraft);
                      }
                      if (event.key === "Escape") cancelRename();
                    }}
                  />
                ) : (
                  <button
                    type="button"
                    className="base-name"
                    disabled={busy || disabled || !!editing}
                    aria-expanded={selected === base.id}
                    aria-controls={`base-panel-${base.id}`}
                    onClick={() => {
                      setSelected((current) =>
                        current === base.id ? "" : base.id,
                      );
                    }}
                  >
                    <span aria-hidden="true">
                      {selected === base.id ? "⌄" : "›"}
                    </span>
                    {base.name}
                  </button>
                )}
                <button
                  id={`rename-${base.id}`}
                  type="button"
                  className="icon-button base-pencil"
                  disabled={busy || disabled || !!editing}
                  aria-label={t("knowledge.rename_base", { name: base.name })}
                  onClick={() => {
                    setEditing(base.id);
                    setRenameDraft(base.name);
                  }}
                >
                  <svg
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    strokeWidth="1.5"
                    aria-hidden="true"
                  >
                    <path d="m15 4 5 5M4 20l5-1L20 8a2 2 0 0 0-5-5L4 14z" />
                  </svg>
                </button>
              </div>
              <div id={`base-panel-${base.id}`} hidden={selected !== base.id}>
                {active?.id === base.id && (
                  <DocumentsPanel
                    key={base.id}
                    organizationId={organizationId}
                    base={active}
                    t={t}
                    language={language}
                    disabled={busy || disabled || !!editing}
                    onBusyChange={changeBusy}
                  />
                )}
              </div>
            </li>
          ))}
      </ul>
      {linked.length === 0 && (
        <p className="panel-empty">{t("assistant.select_knowledge")}</p>
      )}
      {error && (
        <div className="workspace-alert">
          <p role="alert">{t(error)}</p>
          <button
            type="button"
            disabled={busy}
            onClick={() =>
              retry.current
                ? void run(retry.current)
                : void load(lifecycle.current!.signal)
            }
          >
            {t("common.retry")}
          </button>
          {editing && (
            <button type="button" disabled={busy} onClick={cancelRename}>
              {t("common.cancel")}
            </button>
          )}
        </div>
      )}
      {removing && (
        <Modal
          title={t("knowledge.unlink_title")}
          closeLabel={t("common.close")}
          busy={busy}
          onClose={() => setRemoving(false)}
        >
          <p className="modal-intro">{t("knowledge.unlink_help")}</p>
          <ul>
            {bases
              .filter((base) => checked.includes(base.id))
              .map((base) => (
                <li key={base.id}>{base.name}</li>
              ))}
          </ul>
          <button
            type="button"
            className="secondary-button"
            disabled={busy}
            onClick={() => {
              const ids = linked.filter((id) => !checked.includes(id));
              setRemoving(false);
              void run(() => persistLinks(ids));
            }}
          >
            {t("knowledge.unlink_confirm")}
          </button>
        </Modal>
      )}
    </section>
  );
}
