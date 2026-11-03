import { useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { languages, useLocalization } from "./i18n";
import type { Language, MessageKey } from "./i18n";
import PortalEntry from "./PortalEntry";

function EyeIcon({ visible }: { visible: boolean }) {
  return (
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
  );
}
export default function App({
  shouldPlayEntry = false,
}: {
  shouldPlayEntry?: boolean;
}) {
  const { language, setLanguage, t } = useLocalization();
  const [year] = useState(() => new Date().getFullYear());
  const [isPasswordVisible, setPasswordVisible] = useState(false);
  const [errors, setErrors] = useState<{
    username?: MessageKey;
    password?: MessageKey;
  }>({});
  const [isSubmitted, setSubmitted] = useState(false);
  const usernameRef = useRef<HTMLInputElement>(null);
  const passwordRef = useRef<HTMLInputElement>(null);
  const pageTitle = t("login.page_title");
  useEffect(() => {
    document.documentElement.lang = language;
    document.title = pageTitle;
  }, [language, pageTitle]);
  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const usernameMissing = !usernameRef.current?.value.trim();
    const passwordMissing = !passwordRef.current?.value;
    setErrors({
      username: usernameMissing ? "login.username_required" : undefined,
      password: passwordMissing ? "login.password_required" : undefined,
    });
    setSubmitted(!usernameMissing && !passwordMissing);
    if (usernameMissing) usernameRef.current?.focus();
    else if (passwordMissing) passwordRef.current?.focus();
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
              onChange={(event) => {
                const next = event.target.value as Language;
                setLanguage(next);
              }}
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
            <p className="eyebrow login-eyebrow">{t("login.welcome")}</p>
            <h2 id="login-title">{t("login.greeting")}</h2>
            <p className="login-description">{t("login.description")}</p>
            <form noValidate onSubmit={handleSubmit}>
              <div className="field">
                <label htmlFor="username">{t("login.username")}</label>
                <input
                  ref={usernameRef}
                  id="username"
                  name="username"
                  type="text"
                  autoComplete="username"
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
                    setSubmitted(false);
                  }}
                />
                {errors.username && (
                  <p className="field-error" id="username-error">
                    {t(errors.username)}
                  </p>
                )}
              </div>
              <div className="field">
                <label htmlFor="password">{t("login.password")}</label>
                <div className="password-control">
                  <input
                    ref={passwordRef}
                    id="password"
                    name="password"
                    type={isPasswordVisible ? "text" : "password"}
                    autoComplete="current-password"
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
                      setSubmitted(false);
                    }}
                  />
                  <button
                    className="password-toggle"
                    type="button"
                    aria-controls="password"
                    aria-pressed={isPasswordVisible}
                    aria-label={t("login.show_password")}
                    title={t(
                      isPasswordVisible
                        ? "login.hide_password"
                        : "login.show_password",
                    )}
                    onClick={() => setPasswordVisible(!isPasswordVisible)}
                  >
                    <EyeIcon visible={isPasswordVisible} />
                  </button>
                </div>
                {errors.password && (
                  <p className="field-error" id="password-error">
                    {t(errors.password)}
                  </p>
                )}
              </div>
              <button className="submit-button" type="submit">
                <span>{t("login.submit")}</span>
                <span aria-hidden="true">↗</span>
              </button>
              <div
                role="status"
                aria-live="polite"
                className={
                  isSubmitted ? "form-status is-submitted" : "form-status"
                }
              >
                {isSubmitted ? t("login.unavailable") : t("login.preview")}
              </div>
            </form>
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
