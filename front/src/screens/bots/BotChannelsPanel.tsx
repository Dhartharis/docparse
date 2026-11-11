import { useCallback, useEffect, useState } from "react";
import type { FormEvent } from "react";
import { api, ApiError } from "../../services/api";
import type { MessageKey, Translate } from "../../i18n";
import type { SyncState } from "./BotSyncStatus";
import WidgetSettings from "./WidgetSettings";
import HelpPopover from "../../components/HelpPopover";

type KeyInfo = {
  id: string;
  name: string;
  expiresAt: string;
  revokedAt: string | null;
};
export default function BotChannelsPanel({
  organizationId,
  botId,
  t,
  disabled,
  onBusyChange,
  onSyncStatusChange,
}: {
  organizationId: string;
  botId: string;
  t: Translate;
  disabled: boolean;
  onBusyChange: (busy: boolean) => void;
  onSyncStatusChange: (state: SyncState) => void;
}) {
  const root = `/organizations/${organizationId}/bots/${botId}/channels`;
  const [keys, setKeys] = useState<KeyInfo[]>([]);
  const [name, setName] = useState("");
  const [days, setDays] = useState(90);
  const [secret, setSecret] = useState("");
  const [copied, setCopied] = useState(false);
  const [busy, setBusy] = useState(false);
  const [widgetStatus, setWidgetStatus] = useState<SyncState>("saving");
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<MessageKey | null>(null);
  const load = useCallback(
    async (signal?: AbortSignal) => {
      try {
        const value = await api<KeyInfo[]>(root + "/keys", undefined, signal);
        if (!signal?.aborted) {
          setKeys(value);
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
    void Promise.resolve().then(() => {
      if (!controller.signal.aborted) return load(controller.signal);
    });
    return () => controller.abort();
  }, [load]);
  useEffect(
    () =>
      onSyncStatusChange(
        error || widgetStatus === "error"
          ? "error"
          : busy || loading || widgetStatus === "saving"
            ? "saving"
            : widgetStatus,
      ),
    [busy, loading, error, widgetStatus, onSyncStatusChange],
  );
  async function issue(rotateKeyId?: string, keyName = name) {
    if (busy || disabled) return;
    setBusy(true);
    onBusyChange(true);
    setError(null);
    setSecret("");
    setCopied(false);
    try {
      const value = await api<{ key: KeyInfo; secret: string }>(
        root + "/keys",
        { name: keyName, expiresInDays: days, rotateKeyId },
      );
      setSecret(value.secret);
      setName("");
      await load();
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
  async function revoke(id: string) {
    if (busy || disabled) return;
    setBusy(true);
    onBusyChange(true);
    setError(null);
    setSecret("");
    try {
      await api(root + "/keys/" + id, undefined, undefined, "DELETE");
      await load();
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
  function create(event: FormEvent) {
    event.preventDefault();
    void issue();
  }
  const endpoint = `${window.location.origin}/api/v1/organizations/${organizationId}/bots/${botId}/messages`;
  return (
    <section className="assistant-api">
      <div className="help-title">
        <h3>{t("channels.api_title")}</h3>
        <HelpPopover label={t("common.help_about", { name: t("channels.api_title") })}>
          {t("channels.api_help")}
        </HelpPopover>
      </div>
      {error && (
        <div role="alert" className="workspace-alert">
          <p>{t(error)}</p>
          <button type="button" disabled={busy} onClick={() => void load()}>
            {t("common.retry")}
          </button>
        </div>
      )}
      {secret && (
        <div className="channel-secret" role="status">
          <p>{t("channels.secret_once")}</p>
          <code>{secret}</code>
          <button
            type="button"
            className="secondary-button"
            onClick={async () => {
              try {
                await navigator.clipboard.writeText(secret);
                setCopied(true);
              } catch {
                setError("channels.copy_failed");
              }
            }}
          >
            {t(copied ? "channels.copied" : "channels.copy")}
          </button>
          <button
            type="button"
            className="secondary-button"
            onClick={() => setSecret("")}
          >
            {t("common.close")}
          </button>
        </div>
      )}
      <form onSubmit={create}>
        <label className="field">
          {t("channels.key_name")}
          <input
            value={name}
            onChange={(e) => setName(e.target.value)}
            maxLength={100}
            required
            disabled={disabled || busy}
          />
        </label>
        <label className="field">
          {t("channels.expiry_days")}
          <input
            type="number"
            min={1}
            max={365}
            value={days}
            onChange={(e) => setDays(Number(e.target.value))}
            required
            disabled={disabled || busy}
          />
        </label>
        <button
          className="secondary-button"
          disabled={disabled || busy || !name.trim()}
        >
          {t("channels.create")}
        </button>
      </form>
      {loading ? (
        <p role="status">{t("workspace.loading")}</p>
      ) : (
        <ul className="channel-keys">
          {keys.map((key) => (
            <li key={key.id}>
              <div>
                <strong>{key.name}</strong>
                <small>
                  {key.expiresAt.slice(0, 10)} ·{" "}
                  {t(key.revokedAt ? "channels.revoked" : "channels.active")}
                </small>
              </div>
              {!key.revokedAt && (
                <div>
                  <button
                    type="button"
                    disabled={disabled || busy}
                    onClick={() => void issue(key.id, key.name)}
                  >
                    {t("channels.rotate")}
                  </button>
                  <button
                    type="button"
                    disabled={disabled || busy}
                    onClick={() => void revoke(key.id)}
                  >
                    {t("channels.revoke")}
                  </button>
                </div>
              )}
            </li>
          ))}
        </ul>
      )}
      <details className="channel-docs">
        <summary>{t("channels.example")}</summary>
        <p className="panel-note">{t("channels.server_only")}</p>
        <pre>
          <code>{`POST ${endpoint}\nX-DocParse-Key: <secret>\nContent-Type: application/json\n\n{"question":"...","history":[]}`}</code>
        </pre>
        <p className="panel-note">{t("channels.limits")}</p>
      </details>
      <WidgetSettings
        organizationId={organizationId}
        botId={botId}
        t={t}
        onSyncStatusChange={setWidgetStatus}
        onBusyChange={onBusyChange}
      />
    </section>
  );
}
