CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE IF NOT EXISTS documents (
    id uuid PRIMARY KEY,
    type text NOT NULL,
    fields jsonb NOT NULL,
    original_blob_name text NULL,
    original_file_name text NULL,
    original_content_type text NULL,
    created_at timestamp with time zone NOT NULL
);

CREATE TABLE IF NOT EXISTS document_embeddings (
    id uuid PRIMARY KEY,
    document_id uuid NOT NULL REFERENCES documents (id) ON DELETE CASCADE,
    content text NOT NULL,
    embedding vector(1536) NOT NULL,
    created_at timestamp with time zone NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_document_embeddings_document_id
    ON document_embeddings (document_id);
