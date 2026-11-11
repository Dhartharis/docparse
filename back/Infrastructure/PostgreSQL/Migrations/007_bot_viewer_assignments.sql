CREATE TABLE bots.bot_viewers (
    organization_id uuid NOT NULL,
    bot_id uuid NOT NULL,
    user_id uuid NOT NULL,
    PRIMARY KEY (organization_id, bot_id, user_id),
    FOREIGN KEY (organization_id, bot_id) REFERENCES bots.bots (organization_id, id) ON DELETE CASCADE,
    CONSTRAINT bot_viewer_membership FOREIGN KEY (organization_id, user_id)
        REFERENCES access.memberships ("OrganizationId", "UserId") ON DELETE CASCADE
);
CREATE INDEX ix_bot_viewers_user_scope ON bots.bot_viewers (organization_id, user_id);
