import { useCallback, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { languages, useLocalization } from "../../i18n";
import type { Language, MessageKey } from "../../i18n";
import { api, ApiError } from "../../services/api";
import type { Organization } from "../../services/api";
import ChatScreen from "../chat/ChatScreen";
import TeamPanel from "./TeamPanel";
import TrashPanel from "./TrashPanel";
import CostCalculator from "../usage/CostCalculator";
import UsagePanel from "../usage/UsagePanel";
import BillingPanel from "../billing/BillingPanel";

import BotsScreen from "../bots/BotsScreen";
import Modal from "../../components/Modal";

export default function WorkspaceScreen({
  onSignOut,
  preferredOrganizationId,
}: {
  onSignOut: () => void;
  preferredOrganizationId?: string;
}) {
  const { t, language, setLanguage } = useLocalization();
  const [year] = useState(() => new Date().getFullYear());
  const [organizations, setOrganizations] = useState<Organization[]>([]);
  const [organizationId, setOrganizationId] = useState(
    preferredOrganizationId ?? "",
  );
  const [email, setEmail] = useState("");
  const [userName, setUserName] = useState("");
  const [teamOpen, setTeamOpen] = useState(false);
  const [newBusinessOpen, setNewBusinessOpen] = useState(false);
  const [businessError, setBusinessError] = useState<MessageKey | null>(null);
  const businessInput = useRef<HTMLInputElement>(null);
  const businessRequestId = useRef(crypto.randomUUID());
  const [trashOpen, setTrashOpen] = useState(false);
  const [catalogRevision, setCatalogRevision] = useState(0);
  const [usageOpen, setUsageOpen] = useState(false);
  const [billingOpen, setBillingOpen] = useState(false);
  const [calculatorOpen, setCalculatorOpen] = useState(false);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [featureBusy, setFeatureBusy] = useState(false);

  const [error, setError] = useState<MessageKey | null>(null);
  const request = useRef<AbortController | null>(null);
  const name = useRef<HTMLInputElement>(null);
  const invitation = useRef<HTMLInputElement>(null);
  const active = organizations.find(
    (organization) => organization.id === organizationId,
  );
  const load = useCallback(async (signal: AbortSignal) => {
    try {
      const [user, memberships] = await Promise.all([
        api<{ email: string; userName?: string }>(
          "/auth/me",
          undefined,
          signal,
        ),
        api<Organization[]>("/organizations/", undefined, signal),
      ]);
      if (signal.aborted) return;
      setEmail(user.email);
      setUserName(user.userName ?? user.email.split("@")[0]);
      setOrganizations(memberships);
      setError(null);
      setOrganizationId((previous) =>
        memberships.some((entry) => entry.id === previous)
          ? previous
          : (memberships[0]?.id ?? ""),
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
  }, []);
  useEffect(() => {
    const controller = new AbortController();
    request.current = controller;
    void Promise.resolve().then(() => {
      if (!controller.signal.aborted) return load(controller.signal);
    });
    return () => controller.abort();
  }, [load]);
  useEffect(() => {
    document.documentElement.lang = language;
    document.title = t("workspace.page_title");
  }, [language, t]);
  async function onboard(
    event: FormEvent<HTMLFormElement>,
    kind: "create" | "join",
  ) {
    event.preventDefault();
    if (busy) return;
    const value =
      kind === "create"
        ? name.current?.value.trim()
        : invitation.current?.value.trim();
    if (!value) {
      (kind === "create" ? name : invitation).current?.focus();
      return;
    }
    const signal = request.current!.signal;
    setBusy(true);
    setError(null);
    try {
      await api(
        kind === "create" ? "/organizations/" : "/invitations/accept",
        kind === "create" ? { name: value } : { token: value },
        signal,
      );
      await load(signal);
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
  async function createBusiness(event: FormEvent) {
    event.preventDefault();
    if (busy || !businessInput.current?.value.trim()) return;
    setBusy(true);
    setBusinessError(null);
    try {
      const value = await api<Organization>(
        "/organizations/",
        {
          name: businessInput.current.value.trim(),
          requestId: businessRequestId.current,
        },
        request.current!.signal,
      );
      await load(request.current!.signal);
      setOrganizationId(value.id);
      setNewBusinessOpen(false);
    } catch (e) {
      setBusinessError(
        e instanceof ApiError
          ? (`errors.${e.code}` as MessageKey)
          : "errors.server",
      );
    } finally {
      setBusy(false);
    }
  }
  async function signOutEverywhere() {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      await api("/auth/logout-all", {}, request.current?.signal);
      onSignOut();
    } catch (failure) {
      setError(
        failure instanceof ApiError
          ? (`errors.${failure.code}` as MessageKey)
          : "errors.server",
      );
    } finally {
      setBusy(false);
    }
  }
  return (
    <div className="app-shell workspace-shell">
      <header className="site-header">
        <div className="workspace-identity">
          <div className="product-brand">
            <img
              src="/assets/docparse-mark.svg"
              width="52"
              height="52"
              alt="DocParse"
            />
          </div>
          <div>
            <p className="eyebrow">
              {active
                ? t(active.role === "Admin" ? "workspace.admin" : "team.viewer")
                : t("workspace.account")}
            </p>
            <h1>{userName || t("workspace.loading")}</h1>
            {active && (
              <span className="identity-organization">{active.name}</span>
            )}
          </div>
        </div>
        <div className="header-actions">
          <label className="language-control">
            <span className="sr-only">{t("language.label")}</span>
            <select
              value={language}
              onChange={(event) => setLanguage(event.target.value as Language)}
            >
              {languages.map((entry) => (
                <option key={entry.code} value={entry.code}>
                  {entry.displayName}
                </option>
              ))}
            </select>
          </label>
          <details className="account-menu">
            <summary>{t("workspace.account")}</summary>
            <div>
              <p>{email}</p>
              <button
                type="button"
                disabled={busy || featureBusy}
                onClick={(event) => {
                  event.currentTarget
                    .closest("details")
                    ?.removeAttribute("open");
                  businessRequestId.current = crypto.randomUUID();
                  setBusinessError(null);
                  setNewBusinessOpen(true);
                }}
              >
                {t("onboarding.create_business")}
              </button>
              {active?.role === "Admin" && (
                <button
                  type="button"
                  disabled={busy || featureBusy}
                  onClick={(event) => {
                    event.currentTarget.closest("details")?.removeAttribute("open");
                    setBillingOpen(true);
                  }}
                >
                  {t("billing.title")}
                </button>
              )}
              {active?.role === "Admin" && (
                <button
                  type="button"
                  disabled={busy || featureBusy}
                  onClick={(event) => {
                    event.currentTarget
                      .closest("details")
                      ?.removeAttribute("open");
                    setTrashOpen(true);
                  }}
                >
                  {t("trash.title")}
                </button>
              )}
              {active?.role === "Admin" && (
                <button
                  type="button"
                  disabled={busy || featureBusy}
                  onClick={(event) => {
                    event.currentTarget
                      .closest("details")
                      ?.removeAttribute("open");
                    setUsageOpen(true);
                  }}
                >
                  {t("usage.title")}
                </button>
              )}
              {active?.role === "Admin" && (
                <button
                  type="button"
                  disabled={busy || featureBusy}
                  onClick={(event) => {
                    const menu = event.currentTarget.closest("details");
                    menu?.removeAttribute("open");
                    menu?.querySelector("summary")?.focus();
                    setTeamOpen(true);
                  }}
                >
                  {t("team.title")}
                </button>
              )}
              {active?.role === "Admin" && (
                <button
                  type="button"
                  disabled={busy || featureBusy}
                  onClick={(event) => {
                    event.currentTarget
                      .closest("details")
                      ?.removeAttribute("open");
                    setCalculatorOpen(true);
                  }}
                >
                  {t("economics.title")}
                </button>
              )}
              <button
                type="button"
                disabled={busy || featureBusy}
                onClick={onSignOut}
              >
                {t("workspace.sign_out")}
              </button>
              <button
                type="button"
                disabled={busy || featureBusy}
                onClick={() => void signOutEverywhere()}
              >
                {t("workspace.sign_out_all")}
              </button>
            </div>
          </details>
        </div>
      </header>
      {newBusinessOpen && (
        <Modal
          title={t("onboarding.create_business")}
          closeLabel={t("common.close")}
          busy={busy}
          onClose={() => setNewBusinessOpen(false)}
        >
          <form onSubmit={createBusiness}>
            <p className="panel-note">{t("onboarding.separate_business")}</p>
            <div className="field">
              <label htmlFor="new-business-name">
                {t("onboarding.business_name")}
              </label>
              <input
                id="new-business-name"
                ref={businessInput}
                maxLength={200}
                required
                disabled={busy}
              />
            </div>
            {businessError && (
              <p className="workspace-alert" role="alert">
                {t(businessError)}
              </p>
            )}
            <button className="secondary-button" disabled={busy}>
              {t("onboarding.create_business")}
            </button>
          </form>
        </Modal>
      )}
      {loading ? (
        <main className="workspace-loading" role="status">
          {t("workspace.loading")}
        </main>
      ) : (
        <main className="workspace-main">
          {error && (
            <div className="workspace-alert" role="alert">
              <p>{t(error)}</p>
              <button
                type="button"
                onClick={() => void load(request.current!.signal)}
              >
                {t("common.retry")}
              </button>
            </div>
          )}
          {!active ? (
            !error && (
              <section className="onboarding" aria-labelledby="workspace-title">
                <p className="eyebrow">{t("workspace.welcome")}</p>
                <h1 id="workspace-title">{t("workspace.start")}</h1>
                <p className="muted">{t("workspace.start_description")}</p>
                <div className="onboarding-forms">
                  <form onSubmit={(event) => void onboard(event, "create")}>
                    <h2>{t("workspace.create_title")}</h2>
                    <div className="field">
                      <label htmlFor="organization-name">
                        {t("workspace.name")}
                      </label>
                      <input
                        id="organization-name"
                        ref={name}
                        required
                        maxLength={200}
                        disabled={busy}
                      />
                    </div>
                    <button className="submit-button" disabled={busy}>
                      {t(busy ? "auth.working" : "workspace.create")}
                    </button>
                  </form>
                  <form onSubmit={(event) => void onboard(event, "join")}>
                    <h2>{t("workspace.join_title")}</h2>
                    <p className="muted">{t("workspace.join_description")}</p>
                    <div className="field">
                      <label htmlFor="invitation-code">
                        {t("workspace.invitation")}
                      </label>
                      <input
                        id="invitation-code"
                        ref={invitation}
                        type="password"
                        autoComplete="off"
                        required
                        disabled={busy}
                      />
                    </div>
                    <button className="secondary-button" disabled={busy}>
                      {t(busy ? "auth.working" : "workspace.join")}
                    </button>
                  </form>
                </div>
              </section>
            )
          ) : (
            <>
              {organizations.length > 1 && (
                <div className="workspace-toolbar">
                  <div>
                    {organizations.length > 1 && (
                      <label className="workspace-picker">
                        <span>{t("workspace.switch")}</span>
                        <select
                          disabled={featureBusy}
                          value={organizationId}
                          onChange={(event) =>
                            setOrganizationId(event.target.value)
                          }
                        >
                          {organizations.map((entry) => (
                            <option key={entry.id} value={entry.id}>
                              {entry.name}
                            </option>
                          ))}
                        </select>
                      </label>
                    )}
                  </div>
                </div>
              )}
              {active.role === "Admin" ? (
                <BotsScreen
                  key={`${active.id}-${catalogRevision}`}
                  organizationId={organizationId}
                  organization={active}
                  t={t}
                  language={language}
                  onBusyChange={setFeatureBusy}
                />
              ) : (
                <ChatScreen key={active.id} organization={active} t={t} />
              )}
              {trashOpen && active.role === "Admin" && (
                <Modal
                  title={t("trash.title")}
                  closeLabel={t("common.close")}
                  onClose={() => setTrashOpen(false)}
                >
                  <TrashPanel
                    key={active.id}
                    organizationId={active.id}
                    t={t}
                    language={language}
                    onChanged={() => setCatalogRevision((v) => v + 1)}
                  />
                </Modal>
              )}
              {usageOpen && active.role === "Admin" && (
                <Modal
                  title={t("usage.title")}
                  closeLabel={t("common.close")}
                  onClose={() => setUsageOpen(false)}
                >
                  <UsagePanel
                    key={active.id}
                    organizationId={active.id}
                    t={t}
                    language={language}
                  />
                </Modal>
              )}
              {billingOpen && active.role === "Admin" && (
                <Modal title={t("billing.title")} closeLabel={t("common.close")} onClose={() => setBillingOpen(false)}>
                  <BillingPanel key={active.id} organizationId={active.id} t={t} language={language} />
                </Modal>
              )}
              {calculatorOpen && active.role === "Admin" && (
                <Modal
                  title={t("economics.title")}
                  closeLabel={t("common.close")}
                  onClose={() => setCalculatorOpen(false)}
                >
                  <CostCalculator
                    key={active.id}
                    organizationId={active.id}
                    t={t}
                    language={language}
                  />
                </Modal>
              )}
              {teamOpen && active.role === "Admin" && (
                <Modal
                  title={t("team.title")}
                  closeLabel={t("common.close")}
                  busy={featureBusy}
                  onClose={() => setTeamOpen(false)}
                >
                  <TeamPanel
                    language={language}
                    key={active.id}
                    organizationId={active.id}
                    t={t}
                    initiallyOpen
                    onBusyChange={setFeatureBusy}
                    onMembershipChanged={() => load(request.current!.signal)}
                  />
                </Modal>
              )}
            </>
          )}
        </main>
      )}
      <footer className="workspace-footer">
        <p>{t("footer.copyright", { year })}</p>
        <a href="https://fyr-studio.com">
          Fyr Studio <span aria-hidden="true">↗</span>
        </a>
      </footer>
    </div>
  );
}
