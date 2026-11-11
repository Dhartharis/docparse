ALTER TABLE access."AspNetUsers" ADD COLUMN "Locale" varchar(10) NOT NULL DEFAULT 'en';
ALTER TABLE access.invitations ADD COLUMN "Locale" varchar(10) NOT NULL DEFAULT 'en';
ALTER TABLE access.invitations ADD COLUMN "Generation" uuid NOT NULL DEFAULT gen_random_uuid();
ALTER TABLE access.invitations ADD COLUMN "AcceptedUserId" uuid REFERENCES access."AspNetUsers"("Id");
ALTER TABLE access.invitations ADD COLUMN "RevokedAt" timestamptz;
ALTER TABLE access.invitations ADD COLUMN "TokenCiphertext" text;
ALTER TABLE access.invitations ADD COLUMN "LastSentAt" timestamptz;
ALTER TABLE access.invitations ADD COLUMN "Source" varchar(20) NOT NULL DEFAULT 'team';
ALTER TABLE access.invitations ADD CONSTRAINT invitation_org_identity UNIQUE("OrganizationId","Id");
UPDATE access.invitations i SET "AcceptedUserId"=u."Id" FROM access."AspNetUsers" u WHERE i."Email"=u."NormalizedEmail" AND i."AcceptedAt" IS NOT NULL;
ALTER TABLE bots.pending_access ADD COLUMN invitation_id uuid;
ALTER TABLE bots.pending_access ADD COLUMN invitation_generation uuid;
ALTER TABLE bots.pending_access ADD CONSTRAINT pending_invitation_scope FOREIGN KEY(organization_id,invitation_id) REFERENCES access.invitations("OrganizationId","Id");
CREATE TABLE access.mail_outbox (
 "Id" uuid PRIMARY KEY,"InvitationId" uuid REFERENCES access.invitations("Id"),"Generation" uuid,
 "Payload" text NOT NULL,"State" varchar(20) NOT NULL DEFAULT 'pending' CHECK("State" IN('pending','sending','sent','failed','cancelled')),
 "Attempts" integer NOT NULL DEFAULT 0,"NextAttemptAt" timestamptz NOT NULL DEFAULT now(),
 "LeaseId" uuid,"LeaseUntil" timestamptz,"CreatedAt" timestamptz NOT NULL DEFAULT now(),"UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX mail_outbox_due ON access.mail_outbox("State","NextAttemptAt","LeaseUntil");

CREATE UNIQUE INDEX "UniqueNormalizedEmail" ON access."AspNetUsers"("NormalizedEmail") WHERE "NormalizedEmail" IS NOT NULL;
ALTER TABLE access.organizations ADD COLUMN "CreatedByUserId" uuid REFERENCES access."AspNetUsers"("Id");
ALTER TABLE access.organizations ADD COLUMN "CreationRequestId" uuid;
CREATE UNIQUE INDEX organization_creation_request ON access.organizations("CreatedByUserId","CreationRequestId") WHERE "CreationRequestId" IS NOT NULL;
