ALTER TABLE knowledge.document_embeddings ALTER COLUMN embedding TYPE vector;
ALTER TABLE knowledge.document_embeddings ADD COLUMN embedding_profile varchar(300) NOT NULL DEFAULT 'openai:text-embedding-3-small';
ALTER TABLE knowledge.document_embeddings ADD COLUMN page_number integer NULL CHECK (page_number > 0);
ALTER TABLE knowledge.document_embeddings ADD COLUMN chunk_index integer NOT NULL DEFAULT 0 CHECK (chunk_index >= 0);
CREATE INDEX ix_document_embeddings_profile ON knowledge.document_embeddings (embedding_profile);
