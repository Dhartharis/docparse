CREATE SCHEMA knowledge;
CREATE TABLE knowledge.knowledge_bases (
    id uuid PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES access.organizations ("Id"),
    name varchar(200) NOT NULL CHECK (length(trim(name)) > 0),
    UNIQUE (organization_id, id)
);
ALTER TABLE public.documents ADD COLUMN organization_id uuid NULL;
ALTER TABLE public.documents ADD COLUMN knowledge_base_id uuid NULL;
ALTER TABLE public.documents ADD CONSTRAINT document_knowledge_scope
    FOREIGN KEY (organization_id, knowledge_base_id) REFERENCES knowledge.knowledge_bases (organization_id, id);
ALTER TABLE public.documents ADD CONSTRAINT document_scope_pair
    CHECK ((organization_id IS NULL) = (knowledge_base_id IS NULL));
CREATE INDEX ix_documents_knowledge_scope ON public.documents (organization_id, knowledge_base_id);
