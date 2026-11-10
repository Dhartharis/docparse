-- Retain unassigned legacy rows, but enforce tenant ownership on every new write.
ALTER TABLE knowledge.documents ADD CONSTRAINT documents_require_scope
    CHECK (organization_id IS NOT NULL AND knowledge_base_id IS NOT NULL) NOT VALID;
