using Microsoft.EntityFrameworkCore;

namespace DocParse.Modules.Access;

internal sealed class MailOutboxWorker(IServiceScopeFactory scopes,ILogger<MailOutboxWorker> logger):BackgroundService {
 protected override async Task ExecuteAsync(CancellationToken ct){while(!ct.IsCancellationRequested){try{
  await using var scope=scopes.CreateAsyncScope();var db=scope.ServiceProvider.GetRequiredService<AccessDbContext>();var sender=scope.ServiceProvider.GetRequiredService<LocalEmailSender>();var store=scope.ServiceProvider.GetRequiredService<MailOutboxStore>();
  await db.Database.ExecuteSqlRawAsync("""UPDATE access.invitations SET "TokenCiphertext"=NULL WHERE "TokenCiphertext" IS NOT NULL AND("AcceptedAt" IS NOT NULL OR "RevokedAt" IS NOT NULL OR "ExpiresAt"<=now())""",ct);
  await db.Database.ExecuteSqlRawAsync("""UPDATE access.mail_outbox m SET "State"='cancelled',"Payload"='' WHERE "State" IN('pending','sending') AND "InvitationId" IS NOT NULL AND NOT EXISTS(SELECT 1 FROM access.invitations i WHERE i."Id"=m."InvitationId" AND i."Generation"=m."Generation" AND i."AcceptedAt" IS NULL AND i."RevokedAt" IS NULL AND i."ExpiresAt">now())""",ct);
  var lease=Guid.NewGuid();var rows=await db.MailOutbox.FromSqlInterpolated($"""UPDATE access.mail_outbox SET "State"='sending',"LeaseId"={lease},"LeaseUntil"=now()+interval '2 minutes',"Attempts"="Attempts"+1,"UpdatedAt"=now() WHERE "Id"=(SELECT "Id" FROM access.mail_outbox WHERE("State"='pending' AND "NextAttemptAt"<=now()) OR("State"='sending' AND "LeaseUntil"<now()) ORDER BY "CreatedAt" FOR UPDATE SKIP LOCKED LIMIT 1) RETURNING *""").AsNoTracking().ToListAsync(ct);var message=rows.SingleOrDefault();
  if(message is null){await Task.Delay(500,ct);continue;}
  try{var mail=store.Read(message);await sender.SendRawAsync(mail,ct);await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE access.mail_outbox SET "State"='sent',"Payload"='',"LeaseId"=NULL,"LeaseUntil"=NULL,"UpdatedAt"=now() WHERE "Id"={message.Id} AND "LeaseId"={lease} AND "State"='sending'""",ct);}
  catch(Exception e)when(!ct.IsCancellationRequested){logger.LogWarning("Mail attempt {MessageId} failed: {FailureType}",message.Id,e.GetType().Name);await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE access.mail_outbox SET "State"=CASE WHEN "Attempts">=5 THEN 'failed' ELSE 'pending' END,"NextAttemptAt"=now()+CASE "Attempts" WHEN 1 THEN interval '10 seconds' WHEN 2 THEN interval '30 seconds' WHEN 3 THEN interval '120 seconds' ELSE interval '300 seconds' END,"LeaseId"=NULL,"LeaseUntil"=NULL,"UpdatedAt"=now() WHERE "Id"={message.Id} AND "LeaseId"={lease} AND "State"='sending'""",ct);}
  await db.Database.ExecuteSqlRawAsync("""DELETE FROM access.mail_outbox WHERE "State" IN('sent','cancelled','failed') AND "UpdatedAt"<now()-interval '30 days'""",ct);
 }catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}catch(Exception e){logger.LogWarning("Mail outbox iteration failed: {FailureType}",e.GetType().Name);await Task.Delay(5000,ct);}}}
}
