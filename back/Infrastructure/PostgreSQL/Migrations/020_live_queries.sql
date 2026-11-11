CREATE TABLE integrations.live_query_windows (
    connection_id uuid NOT NULL REFERENCES integrations.connections(id),
    window_start timestamptz NOT NULL,
    count integer NOT NULL CHECK(count>0),
    PRIMARY KEY(connection_id,window_start)
);
