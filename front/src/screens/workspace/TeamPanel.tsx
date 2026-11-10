import { useCallback, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import PendingInvitations from "./PendingInvitations";
import { api, ApiError } from "../../services/api";
import type { Language, MessageKey, Translate } from "../../i18n";

export default function TeamPanel({
  organizationId,
  t,
  onMembershipChanged,
  onBusyChange,
  initiallyOpen = false,
  language = "en",
}: {
  language?: Language;
  organizationId: string;
  t: Translate;
  onMembershipChanged: () => Promise<void>;
  onBusyChange: (busy: boolean) => void;
  initiallyOpen?: boolean;
}) {
  const [invitationRevision, setInvitationRevision] = useState(0);
  const [open, setOpen] = useState(initiallyOpen);
  const [members, setMembers] = useState<
    { id: string; email: string; role: string }[]
  >([]);
  const [loading, setLoading] = useState(false);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<MessageKey | null>(null);
  const [error, setError] = useState<MessageKey | null>(null);
  const lifecycle = useRef<AbortController | null>(null);
  const email = useRef<HTMLInputElement>(null);
  const role = useRef<HTMLSelectElement>(null);
  const [confirmation, setConfirmation] = useState<{
    id: string;
    email: string;
    role: "Admin" | "Viewer" | null;
  } | null>(null);
  const actionTrigger = useRef<HTMLButtonElement | null>(null);
  const confirmButton = useRef<HTMLButtonElement>(null);
  const summary = useRef<HTMLElement>(null);
  const load = useCallback(
    async (signal: AbortSignal) => {
      setLoading(true);
      try {
        const value = await api<typeof members>(
          `/organizations/${organizationId}/members`,
          undefined,
          signal,
        );
        if (!signal.aborted) {
          setMembers(value);
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
    if (confirmation) confirmButton.current?.focus();
  }, [confirmation]);
  useEffect(() => {
    const controller = new AbortController();
    lifecycle.current = controller;
    if (open)
      void Promise.resolve().then(async () => {
        if (controller.signal.aborted) return;
        await load(controller.signal);
      });
    return () => controller.abort();
  }, [open, load]);
  async function changeMembership() {
    if (!confirmation || busy) return;
    const signal = lifecycle.current!.signal;
    setBusy(true);
    onBusyChange(true);
    setError(null);
    setMessage(null);
    try {
      await api(
        `/organizations/${organizationId}/members/${confirmation.id}`,
        confirmation.role ? { role: confirmation.role } : undefined,
        signal,
        confirmation.role ? "PATCH" : "DELETE",
      );
      if (signal.aborted) return;
      setConfirmation(null);
      setMessage("team.changed");
      await onMembershipChanged();
      if (!signal.aborted) {
        await load(signal);
        summary.current?.focus();
      }
    } catch (failure) {
      if (!signal.aborted)
        setError(
          failure instanceof ApiError
            ? (`errors.${failure.code}` as MessageKey)
            : "errors.server",
        );
    } finally {
      onBusyChange(false);
      if (!signal.aborted) setBusy(false);
    }
  }
  async function invite(event: FormEvent) {
    event.preventDefault();
    if (busy) return;
    const signal = lifecycle.current!.signal;
    setBusy(true);
    setError(null);
    setMessage(null);
    try {
      await api(
        `/organizations/${organizationId}/invitations`,
        {
          email: email.current!.value.trim(),
          role: role.current!.value,
          locale: language,
        },
        signal,
      );
      if (!signal.aborted) {
        setMessage("team.sent");
        setInvitationRevision((v) => v + 1);
        email.current!.value = "";
      }
    } catch (failure) {
      if (!signal.aborted)
        setError(
          failure instanceof ApiError
            ? (`errors.${failure.code}` as MessageKey)
            : "errors.server",
        );
    } finally {
      if (!signal.aborted) setBusy(false);
    }
  }
  return (
    <details
      className="team-panel"
      open={open}
      onToggle={(event) => {
        if (!event.currentTarget.open && busy) event.currentTarget.open = true;
        else setOpen(event.currentTarget.open);
      }}
    >
      <summary ref={summary}>{t("team.title")}</summary>
      {open && (
        <div>
          <p className="muted">{t("team.description")}</p>
          {loading ? (
            <p role="status">{t("workspace.loading")}</p>
          ) : (
            <ul className="team-members">
              {members.map((member) => (
                <li key={member.id}>
                  <span>{member.email}</span>
                  <span>
                    {t(
                      member.role === "Admin"
                        ? "workspace.admin"
                        : "team.viewer",
                    )}
                  </span>
                  <div className="member-actions">
                    <button
                      type="button"
                      className="text-button"
                      disabled={busy}
                      onClick={(event) => {
                        actionTrigger.current = event.currentTarget;
                        setConfirmation({
                          id: member.id,
                          email: member.email,
                          role: member.role === "Admin" ? "Viewer" : "Admin",
                        });
                      }}
                    >
                      {t(
                        member.role === "Admin"
                          ? "team.make_viewer"
                          : "team.make_admin",
                      )}
                    </button>
                    <button
                      type="button"
                      className="text-button"
                      disabled={busy}
                      onClick={(event) => {
                        actionTrigger.current = event.currentTarget;
                        setConfirmation({
                          id: member.id,
                          email: member.email,
                          role: null,
                        });
                      }}
                    >
                      {t("team.remove")}
                    </button>
                  </div>
                </li>
              ))}
            </ul>
          )}
          {confirmation && (
            <section
              className="membership-confirmation"
              aria-labelledby="membership-confirm-title"
            >
              <h3 id="membership-confirm-title">
                {t(
                  confirmation.role
                    ? "team.confirm_role"
                    : "team.confirm_remove",
                  { email: confirmation.email },
                )}
              </h3>
              <p className="muted">
                {t(
                  confirmation.role === "Admin"
                    ? "team.admin_warning"
                    : confirmation.role === "Viewer"
                      ? "team.viewer_warning"
                      : "team.remove_warning",
                )}
              </p>
              <div className="knowledge-actions">
                <button
                  ref={confirmButton}
                  type="button"
                  className="secondary-button"
                  disabled={busy}
                  onClick={() => void changeMembership()}
                >
                  {t("team.confirm")}
                </button>
                <button
                  type="button"
                  className="text-button"
                  disabled={busy}
                  onClick={() => {
                    setConfirmation(null);
                    actionTrigger.current?.focus();
                  }}
                >
                  {t("common.cancel")}
                </button>
              </div>
            </section>
          )}
          <form
            onSubmit={(event) => void invite(event)}
            className="team-invite"
          >
            <div className="field">
              <label htmlFor="invite-email">{t("login.username")}</label>
              <input
                id="invite-email"
                ref={email}
                type="email"
                required
                maxLength={254}
                disabled={busy}
              />
            </div>
            <label className="workspace-picker">
              <span>{t("team.role")}</span>
              <select ref={role} defaultValue="Viewer" disabled={busy}>
                <option value="Viewer">{t("team.viewer")}</option>
                <option value="Admin">{t("workspace.admin")}</option>
              </select>
            </label>
            <button className="submit-button" disabled={busy}>
              {t(busy ? "auth.working" : "team.invite")}
            </button>
          </form>
          <PendingInvitations
            organizationId={organizationId}
            revision={invitationRevision}
            language={language}
            t={t}
          />
          {message && <p role="status">{t(message)}</p>}
          {error && (
            <p role="alert" className="field-error">
              {t(error)}
            </p>
          )}
        </div>
      )}
    </details>
  );
}
