ALTER TABLE knowledge.documents ADD COLUMN processing_status varchar(20) NOT NULL DEFAULT 'ready' CHECK (processing_status IN ('queued','processing','ready','failed'));
ALTER TABLE knowledge.documents ADD COLUMN processing_attempts integer NOT NULL DEFAULT 0 CHECK (processing_attempts >= 0);
ALTER TABLE knowledge.documents ADD COLUMN next_attempt_at timestamptz NOT NULL DEFAULT now();
ALTER TABLE knowledge.documents ADD COLUMN processing_updated_at timestamptz NOT NULL DEFAULT now();
ALTER TABLE knowledge.documents ADD COLUMN lease_id uuid NULL;
ALTER TABLE knowledge.documents ADD COLUMN lease_until timestamptz NULL;
ALTER TABLE knowledge.documents ADD COLUMN processing_error varchar(80) NULL;
CREATE INDEX ix_documents_processing_queue ON knowledge.documents (next_attempt_at, created_at) WHERE processing_status IN ('queued','processing');
