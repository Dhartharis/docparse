CREATE SCHEMA bots;
CREATE TABLE bots.bots (
    id uuid PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES access.organizations ("Id"),
    name varchar(200) NOT NULL CHECK (length(trim(name)) > 0),
    welcome_message varchar(2000) NOT NULL,
    UNIQUE (organization_id, id)
);
CREATE TABLE bots.bot_knowledge_bases (
    organization_id uuid NOT NULL,
    bot_id uuid NOT NULL,
    knowledge_base_id uuid NOT NULL,
    PRIMARY KEY (organization_id, bot_id, knowledge_base_id),
    FOREIGN KEY (organization_id, bot_id) REFERENCES bots.bots (organization_id, id) ON DELETE CASCADE,
    FOREIGN KEY (organization_id, knowledge_base_id) REFERENCES knowledge.knowledge_bases (organization_id, id)
);
