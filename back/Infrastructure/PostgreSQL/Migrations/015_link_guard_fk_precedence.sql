CREATE OR REPLACE FUNCTION bots.require_active_knowledge_base() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE trashed timestamptz;
BEGIN
 SELECT deleted_at INTO trashed FROM knowledge.knowledge_bases WHERE organization_id=NEW.organization_id AND id=NEW.knowledge_base_id FOR UPDATE;
 IF FOUND AND trashed IS NOT NULL THEN
 RAISE EXCEPTION 'Knowledge base unavailable' USING ERRCODE='23514',CONSTRAINT='active_knowledge_base';
 END IF;
 RETURN NEW;
END $$;
