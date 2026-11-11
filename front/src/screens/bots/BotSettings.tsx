import { useEffect, useRef, useState } from "react";
import type { Bot } from "../../services/api";
import { ApiError } from "../../services/api";
import { bots } from "../../services/bots";
import type { MessageKey, Translate } from "../../i18n";

import type { SyncState } from "./BotSyncStatus";

type Details = { name: string; welcomeMessage: string; accentColor: string };
const details = (bot: Bot): Details => ({
  name: bot.name,
  welcomeMessage: bot.welcomeMessage,
  accentColor: bot.accentColor ?? "#7441ec",
});
const equal = (left: Details, right: Details) =>
  left.name.trim() === right.name.trim() &&
  left.welcomeMessage === right.welcomeMessage &&
  left.accentColor === right.accentColor;

export default function BotSettings({
  organizationId,
  bot,
  t,
  disabled,
  onBusyChange,
  onUpdated,
  onSyncStatusChange,
}: {
  organizationId: string;
  bot: Bot;
  t: Translate;
  disabled: boolean;
  onBusyChange: (value: boolean) => void;
  onUpdated: (value: Bot) => void;
  onSyncStatusChange: (state: SyncState) => void;
}) {
  const [draft, setDraft] = useState(() => details(bot));
  const [status, setStatus] = useState<SyncState>("synced");
  useEffect(() => onSyncStatusChange(status), [status, onSyncStatusChange]);
  const [error, setError] = useState<MessageKey | null>(null);
  const [fileError, setFileError] = useState(false);
  const [fileName, setFileName] = useState("");
  const desired = useRef(draft);
  const saved = useRef(bot);
  const logo = useRef<File | null>(null);
  const controller = useRef<AbortController | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const inFlight = useRef(false);
  const input = useRef<HTMLInputElement>(null);
  const dirty = () =>
    !equal(desired.current, details(saved.current)) || logo.current !== null;

  useEffect(() => {
    const value = new AbortController();
    controller.current = value;
    const warn = (event: BeforeUnloadEvent) => {
      if (dirty()) {
        event.preventDefault();
        event.returnValue = "";
      }
    };
    window.addEventListener("beforeunload", warn);
    return () => {
      value.abort();
      if (timer.current) clearTimeout(timer.current);
      window.removeEventListener("beforeunload", warn);
    };
  }, []);

  function schedule() {
    if (timer.current) clearTimeout(timer.current);
    setError(null);
    if (!dirty() && !inFlight.current) {
      setStatus("synced");
      onBusyChange(false);
      return;
    }
    setStatus(inFlight.current ? "saving" : "pending");
    onBusyChange(true);
    timer.current = setTimeout(() => void sync(), 600);
  }
  async function sync() {
    if (inFlight.current || controller.current?.signal.aborted) return;
    const signal = controller.current!.signal;
    const snapshot = { ...desired.current, name: desired.current.name.trim() };
    if (!snapshot.name) {
      setStatus("error");
      setError("sync.name_required");
      return;
    }
    inFlight.current = true;
    setStatus("saving");
    setError(null);
    let failed = false;
    try {
      if (!equal(snapshot, details(saved.current))) {
        await bots.update(
          organizationId,
          bot.id,
          snapshot.name,
          snapshot.welcomeMessage,
          signal,
          snapshot.accentColor,
        );
        if (signal.aborted) return;
        saved.current = { ...saved.current, ...snapshot };
        onUpdated(saved.current);
      }
      const file = logo.current;
      if (file) {
        const value = await bots.uploadLogo(
          organizationId,
          bot.id,
          file,
          signal,
        );
        if (signal.aborted) return;
        saved.current = { ...saved.current, logoVersion: value.logoVersion };
        onUpdated(saved.current);
        if (logo.current === file) {
          logo.current = null;
          setFileName("");
          if (input.current) input.current.value = "";
        }
      }
    } catch (failure) {
      failed = true;
      if (!signal.aborted) {
        setStatus("error");
        setError(
          failure instanceof ApiError
            ? (`errors.${failure.code}` as MessageKey)
            : "errors.server",
        );
      }
    } finally {
      inFlight.current = false;
      if (!signal.aborted && !failed) {
        if (dirty()) schedule();
        else {
          setStatus("synced");
          onBusyChange(false);
        }
      }
    }
  }
  function edit(value: Details) {
    desired.current = value;
    setDraft(value);
    schedule();
  }
  function discard() {
    if (timer.current) clearTimeout(timer.current);
    const value = details(saved.current);
    desired.current = value;
    setDraft(value);
    logo.current = null;
    setFileName("");
    setError(null);
    setFileError(false);
    setStatus("synced");
    if (input.current) input.current.value = "";
    onBusyChange(false);
  }
  return (
    <div className="assistant-settings">
      <form onSubmit={(event) => event.preventDefault()}>
        <fieldset disabled={disabled}>
          <legend className="sr-only">{t("assistant.settings")}</legend>
          <div className="field settings-logo-field">
            <label htmlFor="bot-logo">{t("bots.logo")}</label>
            <input
              id="bot-logo"
              ref={input}
              type="file"
              accept="image/png"
              aria-label={t("bots.choose_logo")}
              aria-describedby="bot-logo-help"
              aria-invalid={fileError}
              onChange={(event) => {
                const value = event.currentTarget.files?.[0] ?? null;
                if (!value) return;
                if (
                  !/\.png$/i.test(value.name) ||
                  value.size < 33 ||
                  value.size > 1024 * 1024
                ) {
                  setFileError(true);
                  event.currentTarget.value = "";
                  return;
                }
                setFileError(false);
                logo.current = value;
                setFileName(value.name);
                schedule();
              }}
            />
            <p id="bot-logo-help" className="form-help">
              {t("bots.logo_help")}
            </p>
            {fileName && <p className="form-help">{fileName}</p>}
            {fileError && (
              <p role="alert" className="field-error">
                {t("bots.logo_invalid")}
              </p>
            )}
          </div>
          <div className="field">
            <label htmlFor="bot-name">{t("bots.name")}</label>
            <input
              id="bot-name"
              required
              maxLength={200}
              value={draft.name}
              aria-invalid={error === "sync.name_required"}
              onChange={(event) =>
                edit({ ...desired.current, name: event.target.value })
              }
            />
          </div>
          <div className="field">
            <label htmlFor="bot-welcome">{t("bots.welcome")}</label>
            <textarea
              id="bot-welcome"
              rows={3}
              maxLength={2000}
              value={draft.welcomeMessage}
              onChange={(event) =>
                edit({ ...desired.current, welcomeMessage: event.target.value })
              }
            />
          </div>
          <label className="workspace-picker">
            <span>{t("bots.accent")}</span>
            <select
              value={draft.accentColor}
              onChange={(event) =>
                edit({ ...desired.current, accentColor: event.target.value })
              }
            >
              <option value="#7441ec">{t("bots.violet")}</option>
              <option value="#2457c5">{t("bots.blue")}</option>
              <option value="#186a47">{t("bots.green")}</option>
              <option value="#a42d54">{t("bots.rose")}</option>
            </select>
          </label>
        </fieldset>
      </form>
      {error && (
        <div className="sync-error">
          <p role="alert" className="field-error">
            {t(error)}
          </p>
          <p className="form-help">{t("sync.recovery")}</p>
          <button className="text-button" type="button" onClick={schedule}>
            {t("common.retry")}
          </button>
          <button className="text-button" type="button" onClick={discard}>
            {t("sync.discard")}
          </button>
        </div>
      )}
    </div>
  );
}
