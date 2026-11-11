import { useEffect, useId, useRef, useState } from "react";
import { api } from "../../services/api";
import type { Translate } from "../../i18n";
import type { SyncState } from "./BotSyncStatus";
import HelpPopover from "../../components/HelpPopover";

type Settings = { enabled: boolean; origins: string[] };
type Change = { settings: Settings; activated?: string; removed?: string };
export default function WidgetSettings({
  organizationId, botId, t, onSyncStatusChange, onBusyChange,
}: {
  organizationId: string; botId: string; t: Translate;
  onSyncStatusChange: (s: SyncState) => void;
  onBusyChange: (v: boolean) => void;
}) {
  const [settings, setSettings] = useState<Settings>({ enabled: false, origins: [] });
  const [inactive, setInactive] = useState<string[]>([]);
  const [draft, setDraft] = useState("");
  const [ready, setReady] = useState(false);
  const [error, setError] = useState(false);
  const [invalid, setInvalid] = useState(false);
  const [busy, setBusy] = useState(false);
  const [reload, setReload] = useState(0);
  const pending = useRef<Change | null>(null);
  const life = useRef<AbortController | null>(null);
  const inputId = useId();
  const root = `/organizations/${organizationId}/bots/${botId}/channels/embed`;
  useEffect(() => {
    const controller = new AbortController();
    life.current = controller;
    void api<Settings>(root, undefined, controller.signal).then((value) => {
      if (controller.signal.aborted) return;
      setSettings(value);
      setInactive(value.enabled ? [] : value.origins);
      setReady(true);
      setError(false);
      onSyncStatusChange("synced");
    }).catch(() => {
      if (!controller.signal.aborted) {
        setError(true);
        onSyncStatusChange("error");
      }
    });
    return () => controller.abort();
  }, [root, reload, onSyncStatusChange]);

  const active = settings.enabled ? settings.origins : [];
  async function save(change: Change) {
    if (!ready || busy) return;
    const signal = life.current!.signal;
    pending.current = change;
    setBusy(true);
    setError(false);
    setInvalid(false);
    onBusyChange(true);
    onSyncStatusChange("saving");
    try {
      await api(root, change.settings, signal, "PUT");
      if (signal.aborted) return;
      setSettings(change.settings);
      setInactive((previous) => [
        ...previous.filter((origin) => origin !== change.activated),
        ...(change.removed ? [change.removed] : []),
      ]);
      if (change.activated) setDraft((current) => {
        try { return new URL(current.trim()).origin === change.activated ? "" : current; }
        catch { return current; }
      });
      pending.current = null;
      onSyncStatusChange("synced");
    } catch {
      if (!signal.aborted) {
        setError(true);
        onSyncStatusChange("error");
      }
    } finally {
      if (!signal.aborted) {
        setBusy(false);
        onBusyChange(false);
      }
    }
  }
  function activate(value: string) {
    let origin: string;
    try {
      const url = new URL(value.trim());
      const loopback = url.hostname === "localhost" || url.hostname === "[::1]" ||
        /^127(?:\.[0-9]{1,3}){3}$/.test(url.hostname);
      if (!(url.protocol === "https:" || (url.protocol === "http:" && loopback)) ||
        url.username || url.password || url.pathname !== "/" || url.search || url.hash)
        throw new Error("Invalid origin");
      origin = url.origin;
      if (active.includes(origin) || active.length >= 20) throw new Error("Duplicate or limit");
    } catch {
      setInvalid(true);
      return;
    }
    void save({ settings: { enabled: true, origins: [...active, origin] }, activated: origin });
  }
  function deactivate(origin: string) {
    const origins = active.filter((value) => value !== origin);
    void save({ settings: { enabled: origins.length > 0, origins }, removed: origin });
  }
  const snippet = `<script src="${window.location.origin}/api/embed/${botId}/loader.js" defer></script>`;
  function row(origin: string, isActive: boolean) {
    return (
      <div className={`widget-origin-row${isActive ? " is-active" : ""}`} key={origin}>
        <input value={origin} readOnly aria-label={t("channels.widget_site", { site: origin })} />
        <button type="button" disabled={busy || error} aria-pressed={isActive}
          aria-label={t(isActive ? "channels.widget_deactivate" : "channels.widget_activate", { site: origin })}
          onClick={() => isActive ? deactivate(origin) : activate(origin)}>
          <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true">
            <path d="m5 12 4 4L19 6" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
        </button>
      </div>
    );
  }
  return (
    <section className="widget-settings">
      <div className="help-title">
        <h3>{t("channels.widget_title")}</h3>
        <HelpPopover label={t("common.help_about", { name: t("channels.widget_title") })}>
          {t("channels.widget_help")}
        </HelpPopover>
      </div>
      {!ready && !error && <p role="status">{t("workspace.loading")}</p>}
      {error && (
        <div role="alert" className="workspace-alert">
          <p>{t("channels.widget_save_error")}</p>
          <button type="button" disabled={busy} onClick={() => {
            if (pending.current) void save(pending.current);
            else { setError(false); setReload((value) => value + 1); }
          }}>{t("common.retry")}</button>
        </div>
      )}
      <div className="widget-origin-list">
        {active.map((origin) => row(origin, true))}
        {inactive.filter((origin) => !active.includes(origin)).map((origin) => row(origin, false))}
      </div>
      <form noValidate onSubmit={(event) => { event.preventDefault(); activate(draft); }}>
        <label className="widget-origins-label" htmlFor={inputId}>{t("channels.widget_origins")}</label>
        <div className="widget-origin-row">
          <input id={inputId} type="url" value={draft} placeholder="https://example.com"
            autoCapitalize="none" autoCorrect="off" spellCheck={false}
            disabled={!ready || busy || error} aria-invalid={invalid}
            aria-describedby={inputId + "-hint" + (invalid ? " " + inputId + "-error" : "")}
            onChange={(event) => { setDraft(event.target.value); setInvalid(false); }} />
          <button type="submit" disabled={!ready || busy || error || !draft.trim() || active.length >= 20}
            aria-label={t("channels.widget_confirm_site")}>
            <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true">
              <path d="m5 12 4 4L19 6" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
          </button>
        </div>
        <p id={inputId + "-hint"} className="panel-note widget-origin-hint">{t("channels.widget_site_hint")}</p>
        {invalid && <p id={inputId + "-error"} className="workspace-alert" role="alert">{t("channels.widget_error")}</p>}
      </form>
      {settings.enabled && (
        <>
          <p className="panel-note">{t("channels.snippet")}</p>
          <pre className="widget-snippet"><code>{snippet}</code></pre>
        </>
      )}
    </section>
  );
}

