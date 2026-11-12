CREATE TABLE integrations.google_accounts (
    id uuid PRIMARY KEY,
    organization_id uuid NOT NULL,
    user_id uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    protected_tokens text,
    revision uuid NOT NULL,
    needs_reconnect boolean NOT NULL DEFAULT false,
    refresh_lease uuid,
    refresh_lease_until timestamptz,
    disconnected_at timestamptz,
    UNIQUE (organization_id, user_id, id),
    FOREIGN KEY (organization_id, user_id) REFERENCES access.memberships("OrganizationId", "UserId") ON DELETE CASCADE,
    CHECK ((refresh_lease IS NULL) = (refresh_lease_until IS NULL)),
    CHECK (disconnected_at IS NULL OR protected_tokens IS NULL)
);
CREATE TABLE integrations.google_authorizations (
    state_hash bytea PRIMARY KEY CHECK (octet_length(state_hash)=32),
    organization_id uuid NOT NULL,
    user_id uuid NOT NULL,
    account_id uuid NOT NULL,
    protected_verifier text,
    expires_at timestamptz NOT NULL,
    claimed_at timestamptz,
    completed_at timestamptz,
    FOREIGN KEY (organization_id,user_id,account_id) REFERENCES integrations.google_accounts(organization_id,user_id,id) ON DELETE CASCADE,
    CHECK (claimed_at IS NULL OR protected_verifier IS NULL)
);
CREATE INDEX google_accounts_owner ON integrations.google_accounts(organization_id,user_id,created_at);
CREATE INDEX google_authorizations_expiry ON integrations.google_authorizations(expires_at);
