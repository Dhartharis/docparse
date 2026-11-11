CREATE TABLE bots.pending_access (
    organization_id uuid NOT NULL,
    bot_id uuid NOT NULL,
    email varchar(254) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (organization_id, bot_id, email),
    FOREIGN KEY (organization_id, bot_id) REFERENCES bots.bots (organization_id, id) ON DELETE CASCADE
);
