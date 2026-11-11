ALTER TABLE bots.bots ADD COLUMN embed_enabled boolean NOT NULL DEFAULT false;
ALTER TABLE bots.bots ADD COLUMN embed_origins text[] NOT NULL DEFAULT '{}';
ALTER TABLE bots.bots ADD COLUMN embed_version uuid NOT NULL DEFAULT gen_random_uuid();
CREATE TABLE bots.api_keys (
 id uuid PRIMARY KEY, organization_id uuid NOT NULL, bot_id uuid NOT NULL,
 name varchar(100) NOT NULL, secret_hash bytea NOT NULL, created_at timestamptz NOT NULL DEFAULT now(),
 expires_at timestamptz NOT NULL, revoked_at timestamptz, last_used_at timestamptz,
 FOREIGN KEY(organization_id,bot_id) REFERENCES bots.bots(organization_id,id) ON DELETE CASCADE,
 CHECK(octet_length(secret_hash)=32), CHECK(expires_at>created_at)
);
CREATE INDEX api_keys_bot ON bots.api_keys(organization_id,bot_id);
CREATE TABLE bots.channel_windows (
 organization_id uuid NOT NULL, bot_id uuid NOT NULL, subject varchar(200) NOT NULL,
 window_start timestamptz NOT NULL, count integer NOT NULL CHECK(count>0),
 PRIMARY KEY(organization_id,bot_id,subject,window_start),
 FOREIGN KEY(organization_id,bot_id) REFERENCES bots.bots(organization_id,id) ON DELETE CASCADE
);
