import { useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { languages, useLocalization } from "../../i18n";
import type { Language, MessageKey } from "../../i18n";
import PortalEntry from "../../PortalEntry";
import { api, ApiError, publicRequest, signIn } from "../../services/api";

type Mode = "login" | "register" | "onboard" | "forgot" | "reset" | "resend";
export default function AuthScreen({
  shouldPlayEntry = false,
  onAuthenticated,
  sessionExpired = false,
}: {
  shouldPlayEntry?: boolean;
  onAuthenticated: (organizationId?: string) => void;
  sessionExpired?: boolean;
}) {
  const { language, setLanguage, t } = useLocalization();
  const [year] = useState(() => new Date().getFullYear());
  const [invitationToken, setInvitationToken] = useState(
    () => new URLSearchParams(location.hash.slice(1)).get("invite") ?? "",
  );
  const [invitationInfo, setInvitationInfo] = useState<{
    email: string;
    organization: string;
    organizationId: string;
    existingAccount: boolean;
  } | null>(null);
  const businessName = useRef<HTMLInputElement>(null);
  const [mode, setMode] = useState<Mode>(
    invitationToken ? "register" : "login",
  );
  const [visible, setVisible] = useState(false);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<MessageKey | null>(null);
  const [failed, setFailed] = useState(false);
  const [errors, setErrors] = useState<{
    username?: MessageKey;
    password?: MessageKey;
    code?: MessageKey;
  }>({});
  const request = useRef<AbortController | null>(null);
  const username = useRef<HTMLInputElement>(null);
  const password = useRef<HTMLInputElement>(null);
  const code = useRef<HTMLInputElement>(null);
  const handle = useRef<HTMLInputElement>(null);
  const invitation = useRef<HTMLInputElement>(null);
  const needsPassword =
    mode === "login" ||
    mode === "register" ||
    mode === "onboard" ||
    mode === "reset";
  useEffect(() => () => request.current?.abort(), []);
  useEffect(() => {
    if (sessionExpired && mode === "login") username.current?.focus();
  }, [sessionExpired, mode]);
  useEffect(() => {
    document.documentElement.lang = language;
    document.title = t("login.page_title");
  }, [language, t]);
  useEffect(() => {
    const capture = () => {
      const value = new URLSearchParams(location.hash.slice(1)).get("invite");
      if (!value) return;
      request.current?.abort();
      setBusy(false);
      setInvitationToken(value);
      setInvitationInfo(null);
      setMode("register");
      setMessage(null);
      setErrors({});
      if (invitation.current) invitation.current.value = value;
      if (password.current) password.current.value = "";
    };
    window.addEventListener("hashchange", capture);
    if (invitationToken)
      history.replaceState(
        history.state,
        "",
        location.pathname + location.search,
      );
    return () => window.removeEventListener("hashchange", capture);
  }, [invitationToken]);
  useEffect(() => {
    if (invitationToken.length !== 64) return;
    const controller = new AbortController();
    void publicRequest<{
      email: string;
      organization: string;
      organizationId: string;
      existingAccount: boolean;
    }>("/invitations/preview", { token: invitationToken }, controller.signal)
      .then((info) => {
        if (controller.signal.aborted) return;
        setInvitationInfo(info);
        if (username.current) username.current.value = info.email.toLowerCase();
        setMode(info.existingAccount ? "login" : "register");
      })
      .catch(() => {
        if (!controller.signal.aborted) {
          setInvitationInfo(null);
          setFailed(true);
          setMessage("errors.invitation");
        }
      });
    return () => controller.abort();
  }, [invitationToken]);
  useEffect(() => {
    const confirmation = new URLSearchParams(location.hash.slice(1)).get(
      "confirmation",
    );
    if (!confirmation) return;
    queueMicrotask(() => {
      setMode("login");
      setMessage(
        confirmation === "success"
          ? "onboarding.confirmed"
          : "onboarding.confirmation_failed",
      );
      setFailed(confirmation !== "success");
    });
    history.replaceState(
      history.state,
      "",
      location.pathname + location.search,
    );
  }, []);
  function changeMode(next: Mode) {
    if (next !== "register") {
      setInvitationToken("");
      setInvitationInfo(null);
    }
    setMode(next);
    setErrors({});
    setMessage(null);
    setFailed(false);
    setVisible(false);
    if (password.current) password.current.value = "";
    if (code.current) code.current.value = "";
    username.current?.focus();
  }
  async function submit(event: FormEvent) {
    event.preventDefault();
    if (busy) return;
    const email = username.current?.value.trim() ?? "";
    const invalidEmail =
      mode !== "login" &&
      email !== "" &&
      !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email);
    const missingPassword = needsPassword && !password.current?.value;
    const missingCode = mode === "reset" && !code.current?.value.trim();
    setErrors({
      username: !email
        ? "login.username_required"
        : invalidEmail
          ? "auth.email_invalid"
          : undefined,
      password: missingPassword ? "login.password_required" : undefined,
      code: missingCode ? "auth.code_required" : undefined,
    });
    if (!email || invalidEmail) {
      username.current?.focus();
      return;
    }
    if (missingPassword) {
      password.current?.focus();
      return;
    }
    if (missingCode) {
      code.current?.focus();
      return;
    }
    if (mode === "onboard" && !businessName.current?.value.trim()) {
      setFailed(true);
      setMessage("errors.validation");
      businessName.current?.focus();
      return;
    }
    if (
      (mode === "register" || mode === "onboard") &&
      ((mode === "register" && !invitation.current?.value.trim()) ||
        !handle.current?.value.trim())
    ) {
      setFailed(true);
      setMessage(
        mode === "onboard" ? "errors.validation" : "errors.invitation",
      );
      (mode === "register" && !invitation.current?.value.trim()
        ? invitation
        : handle
      ).current?.focus();
      return;
    }
    const controller = new AbortController();
    request.current = controller;
    setBusy(true);
    setMessage(null);
    setFailed(false);
    try {
      const secret = password.current?.value ?? "";
      if (mode === "login") {
        await signIn(email, secret, controller.signal);
        if (!controller.signal.aborted) {
          if (password.current) password.current.value = "";
          if (invitationInfo && invitationToken)
            await api(
              "/invitations/accept",
              { token: invitationToken },
              controller.signal,
            );
          if (!controller.signal.aborted)
            onAuthenticated(invitationInfo?.organizationId);
        }
        return;
      }
      if (mode === "onboard") {
        await publicRequest(
          "/onboarding/register",
          {
            email,
            userName: handle.current!.value.trim(),
            password: secret,
            organizationName: businessName.current!.value.trim(),
            locale: language,
          },
          controller.signal,
        );
        if (!controller.signal.aborted) {
          setMode("login");
          setMessage("auth.check_email");
        }
      } else if (mode === "register") {
        await publicRequest(
          "/invitations/register",
          {
            email,
            password: secret,
            userName: handle.current!.value.trim(),
            token: invitation.current!.value.trim(),
          },
          controller.signal,
        );
        if (!controller.signal.aborted) {
          setInvitationToken("");
          setInvitationInfo(null);
          setMode("login");
          setMessage("auth.check_email");
        }
      } else if (mode === "forgot") {
        await publicRequest(
          "/auth/forgotPassword",
          { email },
          controller.signal,
        );
        if (!controller.signal.aborted) {
          setMode("reset");
          setMessage("auth.recovery_sent");
        }
      } else if (mode === "resend") {
        await publicRequest(
          "/auth/resendConfirmationEmail",
          { email },
          controller.signal,
        );
        if (!controller.signal.aborted) setMessage("auth.check_email");
      } else {
        await publicRequest(
          "/auth/resetPassword",
          { email, resetCode: code.current?.value.trim(), newPassword: secret },
          controller.signal,
        );
        if (!controller.signal.aborted) {
          setMode("login");
          setMessage("auth.reset_done");
        }
      }
      if (password.current) password.current.value = "";
    } catch (failure) {
      if (!controller.signal.aborted) {
        if (
          mode === "onboard" &&
          failure instanceof ApiError &&
          failure.code === "account_exists"
        ) {
          setMode("login");
          setFailed(false);
          setMessage("errors.account_exists");
          if (password.current) password.current.value = "";
          return;
        }
        setFailed(true);
        setMessage(
          failure instanceof ApiError
            ? (`errors.${failure.code}` as MessageKey)
            : "errors.server",
        );
      }
    } finally {
      if (!controller.signal.aborted) setBusy(false);
    }
  }
  return (
    <div className="app-shell">
      <PortalEntry active={shouldPlayEntry} />
      <header className="site-header">
        <a href="/" className="product-brand" aria-label="DocParse">
          <img src="/assets/docparse-mark.svg" alt="" width="44" height="44" />
          <span>
            DocParse<span className="brand-dot">.</span>
          </span>
        </a>
        <div className="header-actions">
          <a className="studio-link" href="https://fyr-studio.com">
            <span aria-hidden="true">↗</span> Fyr Studio
          </a>
          <span className="header-divider" aria-hidden="true" />
          <label className="language-control">
            <svg
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="1.5"
              aria-hidden="true"
            >
              <circle cx="12" cy="12" r="9" />
              <path d="M3 12h18M12 3c5 5 5 13 0 18-5-5-5-13 0-18Z" />
            </svg>
            <span className="sr-only">{t("language.label")}</span>
            <select
              value={language}
              onChange={(event) => setLanguage(event.target.value as Language)}
            >
              {languages.map((entry) => (
                <option key={entry.code} value={entry.code} lang={entry.code}>
                  {entry.displayName}
                </option>
              ))}
            </select>
          </label>
        </div>
      </header>
      <main className="login-layout">
        <section className="introduction" aria-labelledby="intro-title">
          <p className="eyebrow">
            <span className="accent-line" />
            {t("doc.category")}
          </p>
          <h1 id="intro-title">{t("doc.description")}</h1>
          <div className="brand-art" aria-hidden="true">
            <div className="orbit orbit-outer" />
            <div className="orbit orbit-inner" />
            <span className="orbit-point" />
            <img
              src="/assets/docparse-mark.svg"
              alt=""
              width="250"
              height="250"
            />
          </div>
          <div className="knowledge-path" aria-hidden="true">
            <span>{t("intro.documents")}</span>
            <span className="path-line" />
            <span>{t("intro.knowledge")}</span>
            <span className="path-line" />
            <span>{t("intro.conversation")}</span>
          </div>
        </section>
        <section className="login-section" aria-labelledby="login-title">
          <div className="login-content">
            <p className="eyebrow login-eyebrow">
              {t(
                sessionExpired && mode === "login"
                  ? "session.eyebrow"
                  : "login.welcome",
              )}
            </p>
            <h2 id="login-title">
              {t(
                mode === "login"
                  ? sessionExpired
                    ? "session.title"
                    : "login.greeting"
                  : (`auth.${mode}_title` as MessageKey),
              )}
            </h2>
            <p className="login-description">
              {t(
                mode === "login"
                  ? sessionExpired
                    ? "session.description"
                    : "login.description"
                  : (`auth.${mode}_description` as MessageKey),
              )}
            </p>
            {sessionExpired && mode === "login" && (
              <div className="session-notice" role="status">
                <span aria-hidden="true">
                  <svg
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    strokeWidth="1.5"
                  >
                    <path d="M12 3 4 6v6c0 5 8 9 8 9s8-4 8-9V6z" />
                    <path d="M9 11V9a3 3 0 0 1 6 0v2M8 11h8v6H8z" />
                  </svg>
                </span>
                <p>{t("session.notice")}</p>
              </div>
            )}
            {invitationInfo && (
              <p className="invitation-context">
                {t("onboarding.invited_to", {
                  name: invitationInfo.organization,
                })}
              </p>
            )}
            <form
              noValidate
              onSubmit={(event) => void submit(event)}
              aria-busy={busy}
            >
              {mode === "onboard" && (
                <div className="field">
                  <label htmlFor="business-name">
                    {t("onboarding.business_name")}
                  </label>
                  <input
                    id="business-name"
                    ref={businessName}
                    maxLength={200}
                    required
                    disabled={busy}
                    autoComplete="organization"
                  />
                </div>
              )}
              <div className="field">
                <label htmlFor="username">
                  {t(mode === "login" ? "login.identifier" : "login.username")}
                </label>
                <input
                  ref={username}
                  id="username"
                  name="username"
                  type={mode === "login" ? "text" : "email"}
                  disabled={busy}
                  readOnly={Boolean(invitationInfo)}
                  autoComplete={mode === "login" ? "username" : "email"}
                  autoCapitalize="none"
                  spellCheck={false}
                  required
                  placeholder={t("login.username_placeholder")}
                  aria-invalid={Boolean(errors.username)}
                  aria-describedby={
                    errors.username ? "username-error" : undefined
                  }
                  onChange={() => {
                    setErrors((previous) => ({
                      ...previous,
                      username: undefined,
                    }));
                    setMessage(null);
                  }}
                />
                {errors.username && (
                  <p className="field-error" id="username-error">
                    {t(errors.username)}
                  </p>
                )}
              </div>
              {(mode === "register" || mode === "onboard") && (
                <>
                  {mode === "register" && (
                    <div className="field" hidden={Boolean(invitationInfo)}>
                      <label htmlFor="invitation-token">
                        {t("workspace.invitation")}
                      </label>
                      <input
                        id="invitation-token"
                        ref={invitation}
                        defaultValue={invitationToken}
                        type="password"
                        autoComplete="off"
                        disabled={busy}
                        required
                        maxLength={64}
                        onChange={(event) => {
                          if (event.target.value.trim().length === 64)
                            setInvitationToken(event.target.value.trim());
                          else setInvitationInfo(null);
                        }}
                      />
                    </div>
                  )}
                  <div className="field">
                    <label htmlFor="account-handle">{t("auth.handle")}</label>
                    <input
                      id="account-handle"
                      ref={handle}
                      autoComplete="username"
                      disabled={busy}
                      required
                      maxLength={64}
                    />
                    <p className="form-help">{t("auth.handle_help")}</p>
                  </div>
                </>
              )}
              {mode === "reset" && (
                <div className="field">
                  <label htmlFor="reset-code">{t("auth.code")}</label>
                  <input
                    id="reset-code"
                    ref={code}
                    autoComplete="one-time-code"
                    required
                    disabled={busy}
                    aria-invalid={Boolean(errors.code)}
                    aria-describedby={errors.code ? "code-error" : undefined}
                  />
                  {errors.code && (
                    <p id="code-error" className="field-error">
                      {t(errors.code)}
                    </p>
                  )}
                </div>
              )}
              {needsPassword && (
                <div className="field">
                  <div className="field-label-row">
                    <label htmlFor="password">{t("login.password")}</label>
                    {mode === "login" && (
                      <button
                        type="button"
                        className="auth-recovery"
                        disabled={busy}
                        onClick={() => changeMode("forgot")}
                      >
                        {t("auth.forgot_link")}
                      </button>
                    )}
                  </div>
                  <div className="password-control">
                    <input
                      ref={password}
                      id="password"
                      name="password"
                      type={visible ? "text" : "password"}
                      autoComplete={
                        mode === "login" ? "current-password" : "new-password"
                      }
                      disabled={busy}
                      required
                      placeholder={t("login.password_placeholder")}
                      aria-invalid={Boolean(errors.password)}
                      aria-describedby={
                        errors.password ? "password-error" : undefined
                      }
                      onChange={() => {
                        setErrors((previous) => ({
                          ...previous,
                          password: undefined,
                        }));
                        setMessage(null);
                      }}
                    />
                    <button
                      className="password-toggle"
                      type="button"
                      disabled={busy}
                      aria-controls="password"
                      aria-pressed={visible}
                      aria-label={t("login.show_password")}
                      title={t(
                        visible ? "login.hide_password" : "login.show_password",
                      )}
                      onClick={() => setVisible((value) => !value)}
                    >
                      <svg
                        viewBox="0 0 24 24"
                        fill="none"
                        stroke="currentColor"
                        strokeWidth="1.5"
                        aria-hidden="true"
                      >
                        <path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12Z" />
                        <circle cx="12" cy="12" r="3" />
                        {visible && <path d="m3 3 18 18" />}
                      </svg>
                    </button>
                  </div>
                  {errors.password && (
                    <p className="field-error" id="password-error">
                      {t(errors.password)}
                    </p>
                  )}
                  {mode !== "login" && (
                    <p className="form-help">{t("auth.password_help")}</p>
                  )}
                </div>
              )}
              <button className="submit-button" type="submit" disabled={busy}>
                <span>
                  {t(
                    busy
                      ? "auth.working"
                      : mode === "login"
                        ? "login.submit"
                        : (`auth.${mode}_submit` as MessageKey),
                  )}
                </span>
                <span aria-hidden="true">↗</span>
              </button>
              <div
                role={failed ? "alert" : "status"}
                aria-live="polite"
                className={failed ? "form-status field-error" : "form-status"}
              >
                {message && t(message)}
              </div>
            </form>
            {mode === "login" ? (
              <div className="auth-support">
                {!invitationInfo && (
                  <button
                    type="button"
                    className="owner-signup"
                    disabled={busy}
                    onClick={() => changeMode("onboard")}
                  >
                    {t("onboarding.create_business")}{" "}
                    <span aria-hidden="true">↗</span>
                  </button>
                )}
                <div className="auth-support-copy">
                  <span className="auth-support-icon" aria-hidden="true">
                    ↗
                  </span>
                  <p>
                    {t("auth.invitation_heading")}
                    <span>{t("auth.invitation_description")}</span>
                  </p>
                </div>
                <div className="auth-support-actions">
                  <button
                    type="button"
                    disabled={busy}
                    onClick={() => changeMode("register")}
                  >
                    {t("auth.invited_link")}
                    <span aria-hidden="true">↗</span>
                  </button>
                  <button
                    type="button"
                    disabled={busy}
                    onClick={() => changeMode("resend")}
                  >
                    {t("auth.resend_link")}
                  </button>
                </div>
              </div>
            ) : (
              <div className="auth-links">
                <button
                  type="button"
                  disabled={busy}
                  onClick={() => changeMode("login")}
                >
                  {t("auth.back_login")}
                </button>
              </div>
            )}
            <p className="access-hint">
              <svg
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                strokeWidth="1.5"
                aria-hidden="true"
              >
                <rect x="5" y="10" width="14" height="11" rx="3" />
                <path d="M8 10V7a4 4 0 0 1 8 0v3M12 14v3" />
              </svg>
              {t("login.access_hint")}
            </p>
          </div>
        </section>
      </main>
      <footer className="site-footer">
        <a href="https://fyr-studio.com" aria-label={t("studio.back")}>
          <img src="/assets/fyr-studio.svg" width="20" height="20" alt="" />
          {t("footer.signature")}
          <span aria-hidden="true">↗</span>
        </a>
        <p>{t("footer.copyright", { year })}</p>
      </footer>
    </div>
  );
}
