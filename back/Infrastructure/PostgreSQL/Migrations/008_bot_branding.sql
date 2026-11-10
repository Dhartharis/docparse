ALTER TABLE bots.bots
    ADD COLUMN accent_color varchar(7) NOT NULL DEFAULT '#7441ec',
    ADD COLUMN logo_version uuid NULL,
    ADD CONSTRAINT bot_accent_color CHECK (accent_color IN ('#7441ec', '#2457c5', '#186a47', '#a42d54'));
