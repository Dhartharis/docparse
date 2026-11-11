ALTER TABLE integrations.credentials ADD CONSTRAINT integrations_credential_scope_identity UNIQUE(organization_id,connection_id,id);
CREATE TABLE integrations.sync_jobs (
    connection_id uuid PRIMARY KEY,
    organization_id uuid NOT NULL,
    generation uuid NOT NULL,
    configuration_revision uuid NOT NULL,
    credential_id uuid,
    protected_cursor text,
    state varchar(12) NOT NULL CHECK(state IN ('waiting','processing','failed')),
    attempts integer NOT NULL DEFAULT 0 CHECK(attempts BETWEEN 0 AND 5),
    due_at timestamptz NOT NULL DEFAULT now(),
    lease_id uuid,
    lease_until timestamptz,
    error_code varchar(40),
    last_success_at timestamptz,
    last_completed_at timestamptz,
    snapshot_completed boolean NOT NULL DEFAULT false,
    updated_at timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY(organization_id,connection_id) REFERENCES integrations.connections(organization_id,id),
    FOREIGN KEY(organization_id,connection_id,credential_id) REFERENCES integrations.credentials(organization_id,connection_id,id)
);
CREATE INDEX integrations_sync_due ON integrations.sync_jobs(state,due_at,lease_until);

CREATE TABLE operational_data.sync_state (
    organization_id uuid NOT NULL,
    connection_id uuid NOT NULL,
    generation uuid NOT NULL,
    snapshot_id text NOT NULL,
    watermark numeric(38,0) NOT NULL CHECK(watermark>0),
    completed boolean NOT NULL,
    last_version numeric(38,0) NOT NULL CHECK(last_version>0),
    PRIMARY KEY(organization_id,connection_id),
    FOREIGN KEY(organization_id,connection_id) REFERENCES integrations.connections(organization_id,id)
);
CREATE TABLE operational_data.snapshot_seen (
    organization_id uuid NOT NULL,
    connection_id uuid NOT NULL,
    external_id varchar(128) NOT NULL,
    PRIMARY KEY(organization_id,connection_id,external_id),
    FOREIGN KEY(organization_id,connection_id) REFERENCES operational_data.sync_state(organization_id,connection_id)
);
CREATE TABLE operational_data.snapshot_floors (
    organization_id uuid NOT NULL,
    connection_id uuid NOT NULL,
    version numeric(38,0) NOT NULL CHECK(version>0),
    PRIMARY KEY(organization_id,connection_id),
    FOREIGN KEY(organization_id,connection_id) REFERENCES integrations.connections(organization_id,id)
);
