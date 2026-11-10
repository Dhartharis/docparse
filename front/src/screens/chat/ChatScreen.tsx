import { useCallback, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import type { MessageKey, Translate } from "../../i18n";
import { api, ApiError } from "../../services/api";
import type { Answer, Bot, Organization } from "../../services/api";
import BotAvatar from "../bots/BotAvatar";

export type EmbeddedChat = {
  bot: Bot;
  logoUrl?: string;
  send: (
    question: string,
    history: { question: string; answer: string }[],
    signal: AbortSignal,
  ) => Promise<Answer>;
};
type Turn = {
  id: string;
  question: string;
  answer?: Answer;
  error?: MessageKey;
};
export default function ChatScreen({
  organization,
  t,
  botId,
  embedded,
}: {
  organization: Organization;
  t: Translate;
  botId?: string;
  embedded?: EmbeddedChat;
}) {
  const [bots, setBots] = useState<Bot[]>([]);
  const [selected, setSelected] = useState("");
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<MessageKey | null>(null);
  const [threads, setThreads] = useState<Record<string, Turn[]>>({});
  const [draft, setDraft] = useState("");
  const [busy, setBusy] = useState(false);
  const composer = useRef<HTMLTextAreaElement | null>(null);
  const [cleared, setCleared] = useState(false);
  const request = useRef<AbortController | null>(null);
  const lifecycle = useRef<AbortController | null>(null);
  const selectedRef = useRef("");
  const choose = useCallback((id: string) => {
    request.current?.abort();
    setBusy(false);
    setDraft("");
    setCleared(false);
    selectedRef.current = id;
    setSelected(id);
  }, []);
  const active = bots.find((bot) => bot.id === selected);
  const load = useCallback(
    async (signal: AbortSignal) => {
      try {
        const available = embedded
          ? [embedded.bot]
          : await api<Bot[]>(
              `/organizations/${organization.id}/bots/`,
              undefined,
              signal,
            );
        const value = botId
          ? available.filter((bot) => bot.id === botId)
          : available;
        if (signal.aborted) return;
        setBots(value);
        if (!value.some((bot) => bot.id === selectedRef.current))
          choose(value[0]?.id ?? "");
        setError(null);
        setThreads((previous) =>
          Object.fromEntries(
            Object.entries(previous).filter(([id]) =>
              value.some((bot) => bot.id === id),
            ),
          ),
        );
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
    [organization.id, choose, botId, embedded],
  );
  useEffect(() => {
    const controller = new AbortController();
    lifecycle.current = controller;
    void Promise.resolve().then(() => {
      if (!controller.signal.aborted) return load(controller.signal);
    });
    const visible = () => {
      if (!document.hidden) void load(controller.signal);
    };
    document.addEventListener("visibilitychange", visible);
    return () => {
      controller.abort();
      request.current?.abort();
      document.removeEventListener("visibilitychange", visible);
    };
  }, [load]);
  async function send(question: string, turnId: string = crypto.randomUUID()) {
    if (!active || busy) return;
    const botId = active.id;
    const controller = new AbortController();
    request.current = controller;
    setBusy(true);
    setCleared(false);
    setDraft("");
    setThreads((previous) => ({
      ...previous,
      [botId]: [
        ...(previous[botId] ?? []).filter((turn) => turn.id !== turnId),
        { id: turnId, question },
      ],
    }));
    const previous = threads[botId] ?? [];
    const retryIndex = previous.findIndex((turn) => turn.id === turnId);
    const history = (retryIndex < 0 ? previous : previous.slice(0, retryIndex))
      .filter((turn): turn is Turn & { answer: Answer } => Boolean(turn.answer))
      .slice(-4)
      .map((turn) => ({
        question: turn.question.slice(0, 400),
        answer: (turn.answer.code === "no_sources" ? t("chat.no_sources") : turn.answer.answer).slice(0, 800),
      }));
    try {
      const answer = embedded
        ? await embedded.send(question, history, controller.signal)
        : await api<Answer>(
            `/organizations/${organization.id}/bots/${botId}/messages`,
            { question, history },
            controller.signal,
          );
      if (!controller.signal.aborted)
        setThreads((previous) => ({
          ...previous,
          [botId]: (previous[botId] ?? []).map((turn) =>
            turn.id === turnId ? { ...turn, answer } : turn,
          ),
        }));
    } catch (failure) {
      if (controller.signal.aborted) return;
      const key =
        failure instanceof ApiError
          ? (`errors.${failure.code}` as MessageKey)
          : "errors.server";
      setThreads((previous) => ({
        ...previous,
        [botId]: (previous[botId] ?? []).map((turn) =>
          turn.id === turnId ? { ...turn, error: key } : turn,
        ),
      }));
      if (failure instanceof ApiError && failure.code === "access")
        void load(controller.signal);
    } finally {
      if (!controller.signal.aborted) setBusy(false);
    }
  }
  function clearContext() {
    request.current?.abort();
    setBusy(false);
    setThreads((previous) => ({ ...previous, [selected]: [] }));
    setDraft("");
    setCleared(true);
    // Wait for the composer to become enabled after cancelling an answer.
    requestAnimationFrame(() => composer.current?.focus());
  }
  function submit(event: FormEvent) {
    event.preventDefault();
    if (draft.trim()) void send(draft.trim());
  }
  if (loading) return <p role="status">{t("chat.loading")}</p>;
  if (error)
    return (
      <div className="workspace-alert" role="alert">
        <p>{t(error)}</p>
        <button
          type="button"
          onClick={() => {
            void load(lifecycle.current!.signal);
          }}
        >
          {t("common.retry")}
        </button>
      </div>
    );
  if (!active)
    return (
      <section className="workspace-empty">
        <img src="/assets/docparse-mark.svg" width="64" height="64" alt="" />
        <h2>
          {t(
            organization.role === "Viewer"
              ? "chat.no_assigned"
              : "chat.no_bots",
          )}
        </h2>
        <p>
          {t(
            organization.role === "Viewer"
              ? "chat.ask_admin"
              : "chat.no_bots_description",
          )}
        </p>
      </section>
    );
  const turns = threads[active.id] ?? [];
  return (
    <section
      className="chat-panel"
      aria-labelledby="chat-title"
      style={{ borderTop: `3px solid ${active.accentColor ?? "#7441ec"}` }}
    >
      <div className="chat-heading">
        <div>
          <p className="eyebrow">{t("chat.label")}</p>
          <h2 id="chat-title">{active.name}</h2>
        </div>
        <div className="chat-heading-actions">
          {bots.length > 1 && (
            <label className="workspace-picker">
              <span>{t("chat.choose_bot")}</span>
              <select
                value={selected}
                onChange={(event) => choose(event.target.value)}
                disabled={busy}
              >
                {bots.map((bot) => (
                  <option key={bot.id} value={bot.id}>
                    {bot.name}
                  </option>
                ))}
              </select>
            </label>
          )}
          <button
            type="button"
            className="chat-reset"
            title={t("chat.clear_context")}
            aria-label={t("chat.clear_context")}
            disabled={turns.length === 0 && !draft}
            onClick={clearContext}
          >
            <svg
              width="20"
              height="20"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="1.6"
              strokeLinecap="round"
              strokeLinejoin="round"
              aria-hidden="true"
            >
              <path d="M3 10a9 9 0 1 1 2 8M3 4v6h6" />
            </svg>
          </button>
        </div>
      </div>
      <p className="sr-only" role="status">
        {cleared ? t("chat.context_cleared") : ""}
      </p>
      <div
        className="conversation"
        role="log"
        aria-label={t("chat.conversation")}
        aria-live="polite"
      >
        {turns.length === 0 && (
          <div className="chat-welcome">
            <BotAvatar
              organizationId={organization.id}
              bot={active}
              publicSource={embedded?.logoUrl}
            />
            <p>{active.welcomeMessage || t("chat.welcome")}</p>
          </div>
        )}
        {turns.map((turn) => (
          <div className="chat-turn" key={turn.id}>
            <p className="message-user">
              <span className="sr-only">{t("chat.you")}: </span>
              {turn.question}
            </p>
            {turn.answer && (
              <div className="message-answer">
                <p>{turn.answer.code === "no_sources" ? t("chat.no_sources") : turn.answer.answer}</p>
                {turn.answer.sources.length > 0 && (
                  <details className="source-list">
                    <summary>
                      {t("chat.sources", { count: turn.answer.sources.length })}
                    </summary>
                    <ul>
                      {turn.answer.sources.map((source) => (
                        <li
                          key={`${source.documentId}-${source.pageNumber ?? "document"}`}
                        >
                          <span>{source.title}</span>
                          {source.pageNumber != null && (
                            <small>
                              {" "}
                              ·{" "}
                              {t("chat.source_page", {
                                page: source.pageNumber,
                              })}
                            </small>
                          )}
                        </li>
                      ))}
                    </ul>
                  </details>
                )}
              </div>
            )}
            {turn.error && (
              <div className="workspace-alert" role="alert">
                <p>{t(turn.error)}</p>
                <button
                  type="button"
                  disabled={busy}
                  onClick={() => void send(turn.question, turn.id)}
                >
                  {t("common.retry")}
                </button>
              </div>
            )}
          </div>
        ))}
        {busy && (
          <p className="chat-thinking" role="status">
            {t("chat.thinking")}
          </p>
        )}
      </div>
      <form className="chat-compose" onSubmit={submit}>
        <label className="sr-only" htmlFor="question">
          {t("chat.question")}
        </label>
        <textarea
          id="question"
          ref={composer}
          aria-describedby="chat-keyboard-hint"
          enterKeyHint="send"
          onKeyDown={(event) => {
            if (
              event.key === "Enter" &&
              !event.shiftKey &&
              !event.nativeEvent.isComposing &&
              event.keyCode !== 229
            ) {
              event.preventDefault();
              if (!event.repeat && draft.trim()) void send(draft.trim());
            }
          }}
          value={draft}
          onChange={(event) => setDraft(event.target.value)}
          placeholder={t("chat.placeholder")}
          required
          maxLength={8000}
          disabled={busy}
          rows={2}
        />
        <button className="submit-button" disabled={busy || !draft.trim()}>
          {t("chat.send")} <span aria-hidden="true">↗</span>
        </button>
      </form>
      <p id="chat-keyboard-hint" className="chat-keyboard-hint">
        {t("chat.keyboard_hint")}
      </p>
    </section>
  );
}
