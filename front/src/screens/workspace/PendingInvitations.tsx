import { useCallback, useEffect, useState } from "react";
import { api, ApiError } from "../../services/api";
import type { Language, MessageKey, Translate } from "../../i18n";
type Pending = {
  id: string;
  email: string;
  role: string;
  delivery: "pending" | "sending" | "sent" | "failed" | "cancelled" | null;
};
export default function PendingInvitations({
  organizationId,
  revision,
  language,
  t,
}: {
  organizationId: string;
  revision: number;
  language: Language;
  t: Translate;
}) {
  const [rows, setRows] = useState<Pending[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);
  const root = `/organizations/${organizationId}/invitations`;
  const load = useCallback(
    async (signal?: AbortSignal) => {
      try {
        const value = await api<Pending[]>(root, undefined, signal);
        if (!signal?.aborted) {
          setRows(value);
          setError(null);
        }
      } catch (e) {
        if (!signal?.aborted)
          setError(
            e instanceof ApiError
              ? (`errors.${e.code}` as MessageKey)
              : "errors.server",
          );
      }
    },
    [root],
  );
  useEffect(() => {
    const controller = new AbortController();
    void Promise.resolve().then(() => {
      if (!controller.signal.aborted) return load(controller.signal);
    });
    const timer = setInterval(() => {
      if (!document.hidden) void load(controller.signal);
    }, 5000);
    return () => {
      controller.abort();
      clearInterval(timer);
    };
  }, [load, revision]);
  async function act(row: Pending, cancel: boolean) {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      await api(
        cancel ? root + "/" + row.id : root,
        cancel
          ? undefined
          : { email: row.email, role: row.role, locale: language },
        undefined,
        cancel ? "DELETE" : "POST",
      );
      await load();
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
    <section className="pending-invitations">
      <h3>{t("onboarding.pending_title")}</h3>
      {error && (
        <p role="alert" className="workspace-alert">
          {t(error)}
        </p>
      )}
      <ul>
        {rows.map((row) => (
          <li key={row.id}>
            <div>
              <strong>{row.email.toLowerCase()}</strong>
              <small>
                {row.role} · {t(`onboarding.mail_${row.delivery ?? "pending"}`)}
              </small>
            </div>
            <div>
              <button
                type="button"
                disabled={busy}
                onClick={() => void act(row, false)}
              >
                {t("onboarding.resend_invitation")}
              </button>
              <button
                type="button"
                disabled={busy}
                onClick={() => void act(row, true)}
              >
                {t("onboarding.cancel_invitation")}
              </button>
            </div>
          </li>
        ))}
      </ul>
    </section>
  );
}
