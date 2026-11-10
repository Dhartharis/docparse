import { useCallback, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { ApiError } from "../../services/api";
import { knowledge } from "../../services/knowledge";
import type { KnowledgeBase } from "../../services/knowledge";
import type { Language, MessageKey, Translate } from "../../i18n";
import DocumentsPanel from "./DocumentsPanel";

export default function KnowledgeScreen({
  organizationId,
  t,
  language,
  onBusyChange,
}: {
  organizationId: string;
  t: Translate;
  language: Language;
  onBusyChange: (busy: boolean) => void;
}) {
  const [bases, setBases] = useState<KnowledgeBase[]>([]);
  const [selected, setSelected] = useState("");
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [creating, setCreating] = useState(false);
  const [createOpen, setCreateOpen] = useState(false);
  const [renaming, setRenaming] = useState(false);
  const [draftName, setDraftName] = useState("");
  const [error, setError] = useState<MessageKey | null>(null);
  const [createError, setCreateError] = useState<MessageKey | null>(null);
  const name = useRef<HTMLInputElement>(null);
  const createTrigger = useRef<HTMLButtonElement>(null);
  const lifecycle = useRef<AbortController | null>(null);
  const active = bases.find((entry) => entry.id === selected);
  const load = useCallback(
    async (signal: AbortSignal) => {
      try {
        const value = await knowledge.list(organizationId, signal);
        if (!signal.aborted) {
          setBases(value);
          setSelected((current) =>
            value.some((entry) => entry.id === current)
              ? current
              : (value[0]?.id ?? ""),
          );
          setError(null);
        }
      } catch (failure) {
        if (!signal.aborted)
          setError(
            failure instanceof ApiError
              ? (`errors.${failure.code}` as MessageKey)
              : "errors.server",
          );
      } finally {
        if (!signal.aborted) setLoading(false);
      }
    },
    [organizationId],
  );
  useEffect(() => {
    const controller = new AbortController();
    lifecycle.current = controller;
    void Promise.resolve().then(() => {
      if (!controller.signal.aborted) return load(controller.signal);
    });
    return () => controller.abort();
  }, [load]);
  useEffect(() => {
    if (createOpen) name.current?.focus();
  }, [createOpen]);
  function changeBusy(value: boolean) {
    setBusy(value);
    onBusyChange(value);
  }
  async function create(event: FormEvent) {
    event.preventDefault();
    const value = draftName.trim();
    if (!value || busy) return;
    const signal = lifecycle.current!.signal;
    setCreating(true);
    setCreateError(null);
    changeBusy(true);
    try {
      let base: KnowledgeBase;
      if (renaming && active) {
        await knowledge.rename(organizationId, active.id, value, signal);
        base = { ...active, name: value };
      } else base = await knowledge.create(organizationId, value, signal);
      if (!signal.aborted) {
        setBases((previous) =>
          renaming
            ? previous.map((entry) => (entry.id === base.id ? base : entry))
            : [...previous, base],
        );
        setSelected(base.id);
        setCreateOpen(false);
        createTrigger.current?.focus();
      }
    } catch (failure) {
      if (!signal.aborted)
        setCreateError(
          failure instanceof ApiError
            ? (`errors.${failure.code}` as MessageKey)
            : "errors.server",
        );
    } finally {
      if (!signal.aborted) {
        setCreating(false);
        changeBusy(false);
      }
    }
  }
  return (
    <section className="knowledge-screen" aria-labelledby="knowledge-title">
      <div className="feature-heading">
        <div>
          <h2 id="knowledge-title">{t("knowledge.title")}</h2>
          <p className="muted">{t("knowledge.description")}</p>
        </div>
        <div className="knowledge-actions">
          {active && (
            <button
              type="button"
              className="text-button"
              disabled={busy || loading}
              onClick={() => {
                setRenaming(true);
                setDraftName(active.name);
                setCreateOpen(true);
                setCreateError(null);
                name.current?.focus();
              }}
            >
              {t("knowledge.rename")}
            </button>
          )}
          <button
            ref={createTrigger}
            type="button"
            className="secondary-button"
            disabled={busy || loading}
            aria-expanded={createOpen}
            aria-controls="create-knowledge"
            onClick={() => {
              setRenaming(false);
              setDraftName("");
              setCreateOpen((previous) => !previous);
              setCreateError(null);
            }}
          >
            {t("knowledge.new")}
          </button>
        </div>
      </div>
      {createOpen && (
        <form
          id="create-knowledge"
          className="knowledge-create"
          onSubmit={(event) => void create(event)}
        >
          <div className="field">
            <label htmlFor="knowledge-name">{t("knowledge.name")}</label>
            <input
              id="knowledge-name"
              ref={name}
              value={draftName}
              onChange={(event) => setDraftName(event.target.value)}
              required
              maxLength={200}
              disabled={busy}
              placeholder={t("knowledge.name_placeholder")}
            />
          </div>
          <div className="knowledge-actions">
            <button className="submit-button" disabled={busy}>
              {t(
                creating
                  ? "auth.working"
                  : renaming
                    ? "knowledge.save_name"
                    : "knowledge.create",
              )}
            </button>
            <button
              type="button"
              className="text-button"
              disabled={busy}
              onClick={() => {
                setCreateOpen(false);
                createTrigger.current?.focus();
              }}
            >
              {t("common.cancel")}
            </button>
          </div>
          {createError && (
            <p role="alert" className="field-error">
              {t(createError)}
            </p>
          )}
        </form>
      )}
      {loading ? (
        <p role="status">{t("knowledge.loading")}</p>
      ) : error ? (
        <div className="workspace-alert" role="alert">
          <p>{t(error)}</p>
          <button
            type="button"
            onClick={() => void load(lifecycle.current!.signal)}
          >
            {t("common.retry")}
          </button>
        </div>
      ) : bases.length === 0 ? (
        <div className="knowledge-empty">
          <span className="empty-symbol" aria-hidden="true">
            ↳
          </span>
          <h3>{t("knowledge.empty")}</h3>
          <p className="muted">{t("knowledge.empty_description")}</p>
        </div>
      ) : (
        <div className="knowledge-layout">
          <aside
            className="knowledge-sidebar"
            aria-label={t("knowledge.bases")}
          >
            <p className="eyebrow">{t("knowledge.bases")}</p>
            <ul>
              {bases.map((base) => (
                <li key={base.id}>
                  <button
                    type="button"
                    disabled={busy || createOpen}
                    aria-current={base.id === selected ? "true" : undefined}
                    onClick={() => {
                      setSelected(base.id);
                    }}
                  >
                    {base.name}
                    <span aria-hidden="true">↗</span>
                  </button>
                </li>
              ))}
            </ul>
          </aside>
          {active && (
            <DocumentsPanel
              key={active.id}
              organizationId={organizationId}
              base={active}
              t={t}
              language={language}
              onBusyChange={changeBusy}
              disabled={creating}
            />
          )}
        </div>
      )}
    </section>
  );
}
