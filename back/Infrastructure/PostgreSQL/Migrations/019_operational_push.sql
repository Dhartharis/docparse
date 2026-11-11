CREATE SCHEMA operational_data;
CREATE TABLE operational_data.records (
    organization_id uuid NOT NULL,
    connection_id uuid NOT NULL,
    external_id varchar(128) NOT NULL,
    version numeric(38,0) NOT NULL CHECK (version>0),
    is_deleted boolean NOT NULL,
    payload jsonb NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (organization_id, connection_id, external_id),
    FOREIGN KEY (organization_id, connection_id) REFERENCES integrations.connections(organization_id,id)
);
CREATE TABLE integrations.request_nonces (
    connection_id uuid NOT NULL REFERENCES integrations.connections(id),
    request_id uuid NOT NULL,
    expires_at timestamptz NOT NULL,
    PRIMARY KEY (connection_id, request_id)
);
CREATE TABLE integrations.push_windows (
    connection_id uuid NOT NULL REFERENCES integrations.connections(id),
    window_start timestamptz NOT NULL,
    count integer NOT NULL CHECK(count>0),
    PRIMARY KEY (connection_id, window_start)
);
CREATE TABLE integrations.push_inbox (
    id uuid PRIMARY KEY,
    organization_id uuid NOT NULL,
    connection_id uuid NOT NULL,
    event_id uuid NOT NULL,
    body_hash bytea NOT NULL,
    payload jsonb NOT NULL,
    state varchar(30) NOT NULL CHECK(state IN ('accepted','processing','applied','ignored_older_version','failed')),
    attempts integer NOT NULL DEFAULT 0,
    due_at timestamptz NOT NULL DEFAULT now(),
    lease_id uuid,
    lease_until timestamptz,
    error_code varchar(40),
    received_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (connection_id,event_id),
    FOREIGN KEY (organization_id,connection_id) REFERENCES integrations.connections(organization_id,id)
);
CREATE INDEX integrations_push_due ON integrations.push_inbox(state,due_at,lease_until);
