using DocParse.Modules.Access.Contracts;
using Microsoft.EntityFrameworkCore;

namespace DocParse.Modules.Bots.Infrastructure;

internal sealed class BotInvitationActivation(BotsDbContext db,IOrganizationAccess access,IOrganizationInvitations invitations){
 public async Task ApplyAsync(Guid organizationId,CancellationToken ct){
  if(!await access.HasAccessAsync(organizationId,cancellationToken:ct))return;var email=await access.GetVerifiedEmailAsync(ct);if(email is null)return;
  var generations=(await invitations.GetAcceptedGenerationsAsync(organizationId,ct)).ToArray();if(generations.Length==0)return;
  if(!await db.PendingAccess.AnyAsync(p=>p.OrganizationId==organizationId&&p.Email==email&&p.InvitationGeneration!=null&&generations.Contains(p.InvitationGeneration.Value),ct))return;
  await using var tx=await db.Database.BeginTransactionAsync(ct);await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM bots.bots WHERE organization_id={organizationId} ORDER BY id FOR UPDATE",ct);
  var admin=await access.HasAccessAsync(organizationId,true,ct);
  var ids=await db.PendingAccess.Where(p=>p.OrganizationId==organizationId&&p.Email==email&&p.InvitationGeneration!=null&&generations.Contains(p.InvitationGeneration.Value)&&db.Bots.Any(b=>b.Id==p.BotId)).Select(p=>p.BotId).ToArrayAsync(ct);
  foreach(var id in ids){if(!admin)await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO bots.bot_viewers(organization_id,bot_id,user_id) VALUES({organizationId},{id},{access.UserId}) ON CONFLICT DO NOTHING",ct);await db.PendingAccess.Where(p=>p.OrganizationId==organizationId&&p.BotId==id&&p.Email==email).ExecuteDeleteAsync(ct);}
  await tx.CommitAsync(ct);
 }
}
