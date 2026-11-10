import { useCallback, useEffect, useRef, useState } from "react";
import type { CSSProperties, FormEvent } from "react";
import { ApiError } from "../../services/api";
import type { Bot, Organization } from "../../services/api";
import { bots } from "../../services/bots";
import type { Language, MessageKey, Translate } from "../../i18n";
import BotSyncStatus from "./BotSyncStatus";
import type { SyncState } from "./BotSyncStatus";
import BotEditor from "./BotEditor";
import type { AssistantPane } from "./BotEditor";
import BotAvatar from "./BotAvatar";
import Modal from "../../components/Modal";
import ChatScreen from "../chat/ChatScreen";
const panes: AssistantPane[] = ["settings", "knowledge", "access", "api"];
export default function BotsScreen({
  organizationId,
  organization,
  t,
  language,
  onBusyChange,
}: {
  organizationId: string;
  organization: Organization;
  t: Translate;
  language: Language;
  onBusyChange: (value: boolean) => void;
}) {
  const [items, setItems] = useState<Bot[]>([]);
  const [selected, setSelected] = useState("");
  const [syncStatus, setSyncStatus] = useState<SyncState>("synced");
  const [pane, setPane] = useState<AssistantPane>("settings");
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [creating, setCreating] = useState(false);
  const [preview, setPreview] = useState<Bot | null>(null);
  const [error, setError] = useState<MessageKey | null>(null);
  const [createError, setCreateError] = useState<MessageKey | null>(null);
  const name = useRef<HTMLInputElement>(null);
  const welcome = useRef<HTMLTextAreaElement>(null);
  const color = useRef<HTMLSelectElement>(null);
  const lifecycle = useRef<AbortController | null>(null);
  const board = useRef<HTMLDivElement>(null);
  const [columns, setColumns] = useState(1);
  const [position, setPosition] = useState(0);
  const total = items.length ? items.length + 1 : 2;
  const lastPosition = Math.max(0, total - columns);
  useEffect(() => {
    const node = board.current;
    if (!node) return;
    const observer = new ResizeObserver(([entry]) => {
      setColumns(
        entry.contentRect.width < 720 ||
          (window.innerHeight < 500 && window.innerWidth < 1000)
          ? 1
          : Math.max(1, Math.floor((entry.contentRect.width + 16) / 346)),
      );
    });
    observer.observe(node);
    return () => observer.disconnect();
  }, [loading, error]);
  useEffect(() => {
    const frame = requestAnimationFrame(() => {
      board.current
        ?.querySelector<HTMLElement>(".is-selected")
        ?.scrollIntoView({ block: "nearest", inline: "nearest" });
    });
    return () => cancelAnimationFrame(frame);
  }, [selected, columns]);
  function move(direction: number) {
    const node = board.current;
    if (!node || busy) return;
    const step =
      (node.firstElementChild?.getBoundingClientRect().width ??
        node.clientWidth) + 16;
    node.scrollTo({
      left: Math.max(0, Math.min(lastPosition, position + direction)) * step,
    });
  }

  const load = useCallback(
    async (signal: AbortSignal) => {
      try {
        const value = await bots.list(organizationId, signal);
        if (!signal.aborted) {
          setItems(value);
          setSelected((current) =>
            value.some((item) => item.id === current)
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
    const value = new AbortController();
    lifecycle.current = value;
    void Promise.resolve().then(() => {
      if (!value.signal.aborted) return load(value.signal);
    });
    return () => value.abort();
  }, [load]);
  function changeBusy(value: boolean) {
    setBusy(value);
    onBusyChange(value);
  }
  async function create(event: FormEvent) {
    event.preventDefault();
    if (busy || !name.current?.value.trim()) return;
    const signal = lifecycle.current!.signal;
    changeBusy(true);
    setCreateError(null);
    try {
      const value = await bots.create(
        organizationId,
        name.current.value.trim(),
        welcome.current?.value ?? "",
        signal,
        color.current?.value,
      );
      if (!signal.aborted) {
        setItems((previous) => [...previous, value]);
        setSelected(value.id);
        setPane("settings");
        setCreating(false);
      }
    } catch (failure) {
      if (!signal.aborted)
        setCreateError(
          failure instanceof ApiError
            ? (`errors.${failure.code}` as MessageKey)
            : "errors.server",
        );
    } finally {
      if (!signal.aborted) changeBusy(false);
    }
  }
  function selectPane(id: string, value: AssistantPane) {
    if (busy) return;
    setSelected(id);
    setPane(value);
  }
  return (
    <section
      className="assistant-workspace"
      aria-label={t("workspace.assistant_heading")}
    >
      {loading ? (
        <p role="status">{t("bots.loading")}</p>
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
      ) : (
        <>
          <div
            ref={board}
            role="group"
            aria-label={t("assistant.columns")}
            tabIndex={0}
            style={{ "--assistant-columns": columns } as CSSProperties}
            onScroll={(event) => {
              const node = event.currentTarget;
              const step =
                (node.firstElementChild?.getBoundingClientRect().width ??
                  node.clientWidth) + 16;
              setPosition(Math.round(node.scrollLeft / step));
            }}
            className={`assistant-board${items.length === 0 ? " is-empty" : ""}`}
          >
            <button
              className="assistant-create-card"
              type="button"
              disabled={busy}
              aria-haspopup="dialog"
              onClick={() => {
                setCreating(true);
                setCreateError(null);
              }}
            >
              <span className="create-cross" aria-hidden="true">
                +
              </span>
              <span className="create-card-title">{t("assistant.create")}</span>
              <span className="create-card-description">
                {t("assistant.create_description")}
              </span>
              <span className="create-card-bottom" aria-hidden="true">
                ↗
              </span>
            </button>
            {items.length === 0 ? (
              <div className="assistant-empty">
                <img
                  src="/assets/docparse-mark.svg"
                  width="110"
                  height="110"
                  alt=""
                />
                <h2>{t("assistant.empty")}</h2>
                <p>{t("assistant.empty_description")}</p>
              </div>
            ) : (
              items.map((bot) => (
                <article
                  className={`assistant-card${selected === bot.id ? " is-selected" : ""}`}
                  key={bot.id}
                  aria-label={bot.name}
                >
                  <header className="assistant-card-header">
                    <div
                      className="assistant-avatar"
                      style={{ borderColor: bot.accentColor ?? "#7441ec" }}
                    >
                      <BotAvatar organizationId={organizationId} bot={bot} />
                    </div>
                    <div>
                      <span className="card-eyebrow">
                        {t("assistant.label")}
                      </span>
                      <h2>{bot.name}</h2>
                    </div>
                    <BotSyncStatus
                      state={selected === bot.id ? syncStatus : "synced"}
                      t={t}
                    />
                  </header>
                  <div
                    className="assistant-tabs"
                    role="tablist"
                    aria-label={t("assistant.configure", { name: bot.name })}
                  >
                    {panes.map((value) => (
                      <button
                        type="button"
                        role="tab"
                        key={value}
                        id={`tab-${bot.id}-${value}`}
                        aria-selected={
                          (selected === bot.id ? pane : "settings") === value
                        }
                        aria-controls={`panel-${bot.id}`}
                        tabIndex={
                          (selected === bot.id ? pane : "settings") === value
                            ? 0
                            : -1
                        }
                        disabled={busy}
                        onKeyDown={(event) => {
                          if (
                            ["ArrowRight", "ArrowLeft", "Home", "End"].includes(
                              event.key,
                            )
                          ) {
                            event.preventDefault();
                            const index = panes.indexOf(value);
                            const next =
                              event.key === "Home"
                                ? 0
                                : event.key === "End"
                                  ? panes.length - 1
                                  : (index +
                                      (event.key === "ArrowRight" ? 1 : -1) +
                                      panes.length) %
                                    panes.length;
                            selectPane(bot.id, panes[next]);
                            document
                              .getElementById(`tab-${bot.id}-${panes[next]}`)
                              ?.focus();
                          }
                        }}
                        onClick={() => selectPane(bot.id, value)}
                      >
                        {t(`assistant.${value}` as MessageKey)}
                      </button>
                    ))}
                  </div>
                  <div
                    className="assistant-card-body"
                    id={`panel-${bot.id}`}
                    role="tabpanel"
                    tabIndex={0}
                    aria-labelledby={`tab-${bot.id}-${selected === bot.id ? pane : "settings"}`}
                  >
                    {selected === bot.id ? (
                      <BotEditor
                        organizationId={organizationId}
                        bot={bot}
                        pane={pane}
                        t={t}
                        language={language}
                        disabled={creating}
                        onBusyChange={changeBusy}
                        onSyncStatusChange={setSyncStatus}
                        onDeleted={() => {
                          setItems((previous) =>
                            previous.filter((item) => item.id !== bot.id),
                          );
                          setSelected("");
                        }}
                        onUpdated={(value) =>
                          setItems((previous) =>
                            previous.map((item) =>
                              item.id === value.id ? value : item,
                            ),
                          )
                        }
                      />
                    ) : (
                      <div className="card-idle">
                        <p>
                          {bot.welcomeMessage || t("assistant.configure_hint")}
                        </p>
                        <button
                          type="button"
                          className="secondary-button"
                          disabled={busy}
                          onClick={() => selectPane(bot.id, "settings")}
                        >
                          {t("assistant.open")}
                        </button>
                      </div>
                    )}
                  </div>
                  <footer className="assistant-card-footer">
                    <button
                      type="button"
                      disabled={busy}
                      onClick={() => setPreview(bot)}
                    >
                      {t("assistant.preview")}
                      <span aria-hidden="true">↗</span>
                    </button>
                  </footer>
                </article>
              ))
            )}
          </div>
          {lastPosition > 0 && (
            <nav
              className="assistant-pager"
              aria-label={t("assistant.columns")}
            >
              <button
                type="button"
                className="icon-button"
                disabled={busy || position <= 0}
                onClick={() => move(-1)}
                aria-label={t("assistant.previous")}
              >
                ←
              </button>
              <span>
                {t("assistant.column_range", {
                  first: position + 1,
                  last: Math.min(total, position + columns),
                  total,
                })}
              </span>
              <button
                type="button"
                className="icon-button"
                disabled={busy || position >= lastPosition}
                onClick={() => move(1)}
                aria-label={t("assistant.next")}
              >
                →
              </button>
            </nav>
          )}
        </>
      )}
      {creating && (
        <Modal
          title={t("assistant.create")}
          closeLabel={t("common.close")}
          busy={busy}
          onClose={() => setCreating(false)}
        >
          <form onSubmit={(event) => void create(event)}>
            <p className="modal-intro">{t("assistant.create_help")}</p>
            <div className="field">
              <label htmlFor="new-bot-name">{t("bots.name")}</label>
              <input
                id="new-bot-name"
                ref={name}
                required
                maxLength={200}
                autoFocus
                disabled={busy}
              />
            </div>
            <div className="field">
              <label htmlFor="new-bot-welcome">{t("bots.welcome")}</label>
              <textarea
                id="new-bot-welcome"
                ref={welcome}
                rows={3}
                maxLength={2000}
                disabled={busy}
              />
            </div>
            <label className="workspace-picker">
              <span>{t("bots.accent")}</span>
              <select ref={color} defaultValue="#7441ec" disabled={busy}>
                <option value="#7441ec">{t("bots.violet")}</option>
                <option value="#2457c5">{t("bots.blue")}</option>
                <option value="#186a47">{t("bots.green")}</option>
                <option value="#a42d54">{t("bots.rose")}</option>
              </select>
            </label>
            {createError && (
              <p role="alert" className="field-error">
                {t(createError)}
              </p>
            )}
            <button className="submit-button" disabled={busy}>
              {t(busy ? "auth.working" : "assistant.create")}
            </button>
          </form>
        </Modal>
      )}
      {preview && (
        <Modal
          title={preview.name}
          closeLabel={t("common.close")}
          onClose={() => setPreview(null)}
        >
          <ChatScreen organization={organization} t={t} botId={preview.id} />
        </Modal>
      )}
    </section>
  );
}
