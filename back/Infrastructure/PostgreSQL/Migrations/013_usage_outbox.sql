CREATE SCHEMA usage;
CREATE TABLE usage.organization_settings (
 organization_id uuid PRIMARY KEY REFERENCES access.organizations("Id"),
 next_plan_key varchar(80) NOT NULL DEFAULT 'free'
);
CREATE TABLE usage.operations (
 id uuid PRIMARY KEY, organization_id uuid NOT NULL REFERENCES access.organizations("Id"),
 bot_id uuid, feature varchar(80) NOT NULL, channel varchar(40) NOT NULL,
 state varchar(30) NOT NULL CHECK(state IN('reserving','running','pending','dispatching','complete','denied')),
 maximum jsonb NOT NULL, meters jsonb NOT NULL DEFAULT '[]', success boolean NOT NULL DEFAULT false,
 created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
 retry_at timestamptz NOT NULL DEFAULT now(), attempts integer NOT NULL DEFAULT 0,
 lease_id uuid, lease_until timestamptz, error_code varchar(80),
 FOREIGN KEY(organization_id,bot_id) REFERENCES bots.bots(organization_id,id) ON DELETE SET NULL(bot_id)
);
CREATE INDEX usage_outbox_due ON usage.operations(state,retry_at,lease_until);
