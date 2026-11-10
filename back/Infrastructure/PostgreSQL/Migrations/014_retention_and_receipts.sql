ALTER TABLE bots.bots ADD COLUMN deleted_at timestamptz;
ALTER TABLE knowledge.knowledge_bases ADD COLUMN deleted_at timestamptz;
ALTER TABLE knowledge.documents ADD COLUMN deleted_at timestamptz;
ALTER TABLE knowledge.documents ADD COLUMN content_hash bytea;
ALTER TABLE knowledge.documents ADD COLUMN hash_retry_at timestamptz NOT NULL DEFAULT now();
ALTER TABLE knowledge.documents ADD COLUMN original_size_bytes bigint CHECK(original_size_bytes>=0);
CREATE UNIQUE INDEX document_active_content ON knowledge.documents(organization_id,knowledge_base_id,content_hash) WHERE content_hash IS NOT NULL AND deleted_at IS NULL;
CREATE TABLE knowledge.file_cleanup (
 document_id uuid PRIMARY KEY, due_at timestamptz NOT NULL DEFAULT now()+interval '1 day',
 lease_id uuid, lease_until timestamptz, attempts integer NOT NULL DEFAULT 0
);
CREATE TABLE bots.logo_cleanup (
 blob_key text PRIMARY KEY, due_at timestamptz NOT NULL DEFAULT now()+interval '1 day',
 lease_id uuid, lease_until timestamptz, attempts integer NOT NULL DEFAULT 0
);
ALTER TABLE usage.operations ADD COLUMN resource_id uuid;
UPDATE usage.operations SET resource_id=bot_id;
CREATE FUNCTION bots.require_active_knowledge_base() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM knowledge.knowledge_bases WHERE organization_id=NEW.organization_id AND id=NEW.knowledge_base_id AND deleted_at IS NULL FOR UPDATE) THEN
 RAISE EXCEPTION 'Knowledge base unavailable' USING ERRCODE='23514',CONSTRAINT='active_knowledge_base';
 END IF;RETURN NEW;
END $$;
CREATE TRIGGER active_knowledge_base BEFORE INSERT OR UPDATE ON bots.bot_knowledge_bases FOR EACH ROW EXECUTE FUNCTION bots.require_active_knowledge_base();
CREATE FUNCTION knowledge.prevent_linked_trash() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NEW.deleted_at IS NOT NULL AND OLD.deleted_at IS NULL AND EXISTS(SELECT 1 FROM bots.bot_knowledge_bases WHERE organization_id=NEW.organization_id AND knowledge_base_id=NEW.id) THEN
 RAISE EXCEPTION 'Knowledge base is linked' USING ERRCODE='23514',CONSTRAINT='knowledge_base_linked';
 END IF;RETURN NEW;
END $$;
CREATE TRIGGER knowledge_base_linked BEFORE UPDATE OF deleted_at ON knowledge.knowledge_bases FOR EACH ROW EXECUTE FUNCTION knowledge.prevent_linked_trash();
