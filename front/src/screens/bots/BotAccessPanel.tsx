import type { SyncState } from "./BotSyncStatus";
import { useCallback, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { bots } from "../../services/bots";
import type { BotAccess } from "../../services/bots";
import { api, ApiError } from "../../services/api";
import type { Language, MessageKey, Translate } from "../../i18n";
import Modal from "../../components/Modal";

export default function BotAccessPanel({
  organizationId,
  botId,
  t,
  disabled,
  onBusyChange,
  onSyncStatusChange,
  language = "en",
}: {
  language?: Language;
  organizationId: string;
  botId: string;
  t: Translate;
  disabled: boolean;
  onBusyChange: (value: boolean) => void;
  onSyncStatusChange: (value: SyncState) => void;
}) {
  const [entries, setEntries] = useState<BotAccess[]>([]);
  const [query, setQuery] = useState("");
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [adding, setAdding] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);
  const [modalError, setModalError] = useState<MessageKey | null>(null);
  const [message, setMessage] = useState<MessageKey | null>(null);
  const lifecycle = useRef<AbortController | null>(null);
  const email = useRef<HTMLInputElement>(null);
  const all = useRef<HTMLInputElement>(null);
  const addButton = useRef<HTMLButtonElement>(null);
  useEffect(
    () =>
      onSyncStatusChange(
        busy || loading ? "saving" : error || modalError ? "error" : "synced",
      ),
    [busy, loading, error, modalError, onSyncStatusChange],
  );

  const load = useCallback(
    async (signal: AbortSignal) => {
      try {
        const value = await bots.access(organizationId, botId, signal);
        if (!signal.aborted) {
          setEntries(value);
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
    [organizationId, botId],
  );
  useEffect(() => {
    const controller = new AbortController();
    lifecycle.current = controller;
    void Promise.resolve().then(() => {
      if (!controller.signal.aborted) return load(controller.signal);
    });
    return () => controller.abort();
  }, [load]);
  async function add(event: FormEvent) {
    event.preventDefault();
    if (busy || disabled) return;
    const signal = lifecycle.current!.signal;
    setBusy(true);
    onBusyChange(true);
    setModalError(null);
    setMessage(null);
    try {
      const result = await bots.addAccess(
        organizationId,
        botId,
        email.current!.value.trim(),
        Boolean(all.current?.checked),
        signal,
        language,
      );
      if (signal.aborted) return;
      setMessage(
        result.pending ? "access.pending_saved" : "bots.viewers_saved",
      );
      await load(signal);
      if (!signal.aborted) setAdding(false);
    } catch (failure) {
      if (!signal.aborted)
        setModalError(
          failure instanceof ApiError
            ? (`errors.${failure.code}` as MessageKey)
            : "errors.server",
        );
    } finally {
      if (!signal.aborted) {
        setBusy(false);
        onBusyChange(false);
      }
    }
  }
  async function resend(entry: BotAccess) {
    if (busy || disabled) return;
    setBusy(true);
    onBusyChange(true);
    setError(null);
    setMessage(null);
    try {
      await api(
        `/organizations/${organizationId}/bots/${botId}/access/resend`,
        { email: entry.email, locale: language },
        lifecycle.current!.signal,
      );
      setMessage("team.sent");
    } catch (e) {
      setError(
        e instanceof ApiError
          ? (`errors.${e.code}` as MessageKey)
          : "errors.server",
      );
    } finally {
      setBusy(false);
      onBusyChange(false);
    }
  }
  async function remove(entry: BotAccess) {
    if (busy || disabled) return;
    const signal = lifecycle.current!.signal;
    setBusy(true);
    onBusyChange(true);
    setError(null);
    setMessage(null);
    try {
      await bots.removeAccess(organizationId, botId, entry.email, signal);
      if (!signal.aborted) {
        setEntries((previous) =>
          previous.filter((value) => value.email !== entry.email),
        );
        setMessage("access.removed");
        addButton.current?.focus();
      }
    } catch (failure) {
      if (!signal.aborted)
        setError(
          failure instanceof ApiError
            ? (`errors.${failure.code}` as MessageKey)
            : "errors.server",
        );
    } finally {
      if (!signal.aborted) {
        setBusy(false);
        onBusyChange(false);
      }
    }
  }
  const filtered = entries.filter((entry) =>
    entry.email.toLocaleLowerCase().includes(query.trim().toLocaleLowerCase()),
  );
  return (
    <section className="bot-access" aria-label={t("assistant.access")}>
      <div className="access-toolbar">
        <label className="access-search">
          <span className="sr-only">{t("access.search")}</span>
          <input
            type="search"
            value={query}
            placeholder={t("access.search")}
            onChange={(event) => setQuery(event.target.value)}
          />
        </label>
        <button
          ref={addButton}
          type="button"
          className="compact-add"
          disabled={busy || disabled}
          aria-label={t("access.add")}
          onClick={() => {
            setAdding(true);
            setModalError(null);
          }}
        >
          +
        </button>
      </div>
      {loading ? (
        <p role="status">{t("workspace.loading")}</p>
      ) : filtered.length ? (
        <ul className="access-list">
          {filtered.map((entry) => (
            <li key={entry.email}>
              <div>
                <span>{entry.email.toLowerCase()}</span>
                {entry.pending && <small>{t("access.pending")}</small>}
              </div>
              {entry.pending && (
                <button
                  type="button"
                  className="access-resend"
                  disabled={busy || disabled}
                  onClick={() => void resend(entry)}
                >
                  {t("onboarding.resend_invitation")}
                </button>
              )}
              <button
                type="button"
                className="icon-button"
                aria-label={t("access.remove", {
                  email: entry.email.toLowerCase(),
                })}
                disabled={busy || disabled}
                onClick={() => void remove(entry)}
              >
                <svg
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="1.5"
                  aria-hidden="true"
                >
                  <path d="M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13M10 10v7m4-7v7" />
                </svg>
              </button>
            </li>
          ))}
        </ul>
      ) : (
        <p className="panel-empty">
          {t(query ? "access.no_results" : "access.empty")}
        </p>
      )}
      <p className="panel-note">{t("access.admin_note")}</p>
      {message && (
        <p role="status" className="document-success">
          {t(message)}
        </p>
      )}
      {error && (
        <div className="workspace-alert" role="alert">
          <p>{t(error)}</p>
          <button
            type="button"
            onClick={() => void load(lifecycle.current!.signal)}
          >
            {t("common.retry")}
          </button>
        </div>
      )}
      {adding && (
        <Modal
          title={t("access.add")}
          closeLabel={t("common.close")}
          busy={busy}
          onClose={() => setAdding(false)}
        >
          <form onSubmit={(event) => void add(event)}>
            <div className="field">
              <label htmlFor="grant-email">{t("login.username")}</label>
              <input
                ref={email}
                id="grant-email"
                type="email"
                required
                maxLength={254}
                autoFocus
                disabled={busy}
                placeholder="name@example.com"
              />
            </div>
            <label className="assignment-option">
              <input ref={all} type="checkbox" disabled={busy} />
              <span>{t("access.all_current")}</span>
            </label>
            <p className="form-help">{t("access.scope_help")}</p>
            <p className="form-help">{t("access.pending_help")}</p>
            {modalError && (
              <p className="field-error" role="alert">
                {t(modalError)}
              </p>
            )}
            <button className="submit-button" disabled={busy}>
              {t(busy ? "auth.working" : "access.confirm")}
            </button>
          </form>
        </Modal>
      )}
    </section>
  );
}
