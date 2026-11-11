using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using DocParse.Modules.Access.Contracts;
using Microsoft.EntityFrameworkCore;

namespace DocParse.Modules.Access;

public sealed class InvitationUnavailable:Exception;
public sealed class InvitationInvalid:Exception;
public sealed class InvitationMemberExists:Exception;
public sealed class InvitationCooldown:Exception;

internal sealed class InvitationAdministration(AccessDbContext db,IOrganizationAccess access,MailOutboxStore outbox,LocalEmailSender mail):IOrganizationInvitations
{
 public Task<InvitationReceipt> EnsureViewerAsync(Guid org,string email,string locale,CancellationToken ct)=>InviteAsync(org,email,"Viewer",locale,"bot",false,ct);
 public Task<InvitationReceipt> ResendViewerAsync(Guid org,string email,string locale,CancellationToken ct)=>InviteAsync(org,email,"Viewer",locale,"bot",true,ct);
 public async Task<IReadOnlyCollection<Guid>> GetAcceptedGenerationsAsync(Guid org,CancellationToken ct){if(!await access.HasAccessAsync(org,cancellationToken:ct))return [];return await db.Invitations.Where(i=>i.OrganizationId==org&&i.AcceptedUserId==access.UserId&&i.AcceptedAt!=null&&i.RevokedAt==null).Select(i=>i.Generation).ToArrayAsync(ct);}
 public async Task<InvitationReceipt> InviteAsync(Guid org,string email,string role,string locale,string source,bool resend,CancellationToken ct){
  if(!await access.HasAccessAsync(org,true,ct))throw new InvitationUnavailable();
  email=email?.Trim()??"";if(email.Length>254||!MailAddress.TryCreate(email,out var parsed)||parsed.Address!=email||role is not("Admin" or "Viewer"))throw new InvitationInvalid();
  var normalized=email.ToUpperInvariant();await using var tx=await db.Database.BeginTransactionAsync(ct);var lockKey=org+":"+normalized;
  await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey},0))",ct);
  if(await(from m in db.Memberships join u in db.Users on m.UserId equals u.Id where m.OrganizationId==org&&u.NormalizedEmail==normalized select m).AnyAsync(ct))throw new InvitationMemberExists();
  var invitation=await db.Invitations.SingleOrDefaultAsync(i=>i.OrganizationId==org&&i.Email==normalized,ct);
  var fresh=invitation is null||invitation.AcceptedAt!=null||invitation.RevokedAt!=null||invitation.ExpiresAt<=DateTime.UtcNow||invitation.TokenCiphertext is null;
  if(!fresh&&!resend)return new(invitation!.Id,invitation.Generation,invitation.ExpiresAt);
  if(!fresh&&invitation!.LastSentAt>DateTime.UtcNow.AddSeconds(-60)&&invitation.Role==role&&invitation.Source==source)throw new InvitationCooldown();
  if(invitation is null){invitation=new(){OrganizationId=org,Email=normalized};db.Invitations.Add(invitation);}
  string token;
  if(fresh){token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));invitation.Generation=Guid.NewGuid();invitation.TokenHash=Hash(token);invitation.TokenCiphertext=outbox.ProtectToken(token);invitation.ExpiresAt=DateTime.UtcNow.AddDays(7);invitation.AcceptedAt=null;invitation.AcceptedUserId=null;invitation.RevokedAt=null;}
  else token=outbox.ReadToken(invitation.TokenCiphertext!);
  if(source=="bot"&&!fresh){role=invitation.Role;source=invitation.Source;}
  invitation.Role=role;invitation.Source=source;invitation.LastSentAt=DateTime.UtcNow;
  var existingLocale=await db.Users.Where(u=>u.NormalizedEmail==normalized).Select(u=>u.Locale).SingleOrDefaultAsync(ct);invitation.Locale=EmailTemplates.Locale(existingLocale??locale);
  var organization=await db.Organizations.Where(o=>o.Id==org).Select(o=>o.Name).SingleAsync(ct);
  db.MailOutbox.Add(mail.PrepareInvitation(invitation,email,token,organization));await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return new(invitation.Id,invitation.Generation,invitation.ExpiresAt);
 }
 internal static string Hash(string token)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
