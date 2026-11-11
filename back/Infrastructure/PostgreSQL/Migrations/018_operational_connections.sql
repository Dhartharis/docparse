CREATE SCHEMA integrations;

CREATE TABLE integrations.connections (
    id uuid PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES access.organizations("Id"),
    request_id uuid NOT NULL,
    definition_hash bytea NOT NULL,
    name varchar(100) NOT NULL,
    source_business_id varchar(128) NOT NULL,
    base_url text,
    push boolean NOT NULL,
    pull_live boolean NOT NULL,
    pull_scheduled boolean NOT NULL,
    price_max_age_seconds integer NOT NULL CHECK (price_max_age_seconds BETWEEN 1 AND 604800),
    stock_max_age_seconds integer NOT NULL CHECK (stock_max_age_seconds BETWEEN 1 AND 86400),
    sync_interval_seconds integer,
    clock_skew_seconds integer NOT NULL CHECK (clock_skew_seconds BETWEEN 0 AND 300),
    revision uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    disconnected_at timestamptz,
    UNIQUE (organization_id, id),
    UNIQUE (organization_id, request_id),
    CHECK (push OR pull_live OR pull_scheduled),
    CHECK (((pull_live OR pull_scheduled) AND base_url IS NOT NULL) OR (NOT pull_live AND NOT pull_scheduled AND base_url IS NULL)),
    CHECK ((pull_scheduled AND sync_interval_seconds IS NOT NULL AND sync_interval_seconds BETWEEN 60 AND 86400)
        OR (NOT pull_scheduled AND sync_interval_seconds IS NULL))
);

CREATE TABLE integrations.credentials (
    id uuid PRIMARY KEY,
    organization_id uuid NOT NULL,
    connection_id uuid NOT NULL,
    direction varchar(4) NOT NULL CHECK (direction IN ('push', 'pull')),
    key_id varchar(128) NOT NULL,
    protected_secret text,
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz,
    revoked_at timestamptz,
    FOREIGN KEY (organization_id, connection_id) REFERENCES integrations.connections(organization_id, id),
    CHECK (revoked_at IS NOT NULL OR protected_secret IS NOT NULL),
    CHECK (direction <> 'push' OR expires_at IS NOT NULL)
);
CREATE UNIQUE INDEX integrations_one_current_credential ON integrations.credentials(connection_id, direction) WHERE revoked_at IS NULL;
CREATE UNIQUE INDEX integrations_push_key_id ON integrations.credentials(key_id) WHERE direction='push';
CREATE INDEX integrations_connections_tenant ON integrations.connections(organization_id, created_at, id);
