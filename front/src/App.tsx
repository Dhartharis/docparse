import { useEffect, useState } from "react";
import AuthScreen from "./screens/auth/AuthScreen";
import WorkspaceScreen from "./screens/workspace/WorkspaceScreen";
import { clearSession, onSessionExpired } from "./services/api";
import PortalEntry from "./PortalEntry";

export default function App({
  shouldPlayEntry = false,
}: {
  shouldPlayEntry?: boolean;
}) {
  const [preferredOrganization, setPreferredOrganization] = useState<string | undefined>();
  const [authenticated, setAuthenticated] = useState(false);
  const [expired, setExpired] = useState(false);
  const [entering, setEntering] = useState(false);
  useEffect(
    () =>
      onSessionExpired(() => {
        clearSession();
        setExpired(true);
        setEntering(false);
        setAuthenticated(false);
      }),
    [],
  );
  if (authenticated)
    return (
      <>
        <PortalEntry active={entering} />
        <WorkspaceScreen
          preferredOrganizationId={preferredOrganization}
          onSignOut={() => {
            clearSession();
            setExpired(false);
            setAuthenticated(false);
            setEntering(false);
          }}
        />
      </>
    );
  return (
    <AuthScreen
      shouldPlayEntry={shouldPlayEntry && !expired}
      sessionExpired={expired}
      onAuthenticated={(organizationId) => {
        setPreferredOrganization(organizationId);
        setExpired(false);
        setEntering(true);
        setAuthenticated(true);
      }}
    />
  );
}
