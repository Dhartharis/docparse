using System.Net.Mail;
using DocParse.Modules.Access.Contracts;
using Microsoft.EntityFrameworkCore;

namespace DocParse.Modules.Bots.Endpoints;

internal static class BotAccessEndpoints
{
    public static void MapBotAccess(this WebApplication app)
    {
        var group = app.MapGroup("/api/organizations/{organizationId:guid}/bots/{botId:guid}/access").RequireAuthorization();
        group.MapGet("/", async (Guid organizationId, Guid botId, IOrganizationAccess access, BotsDbContext db, CancellationToken ct) =>
        {
            if (!await IsAdmin(organizationId, botId, access, db, ct)) return Results.NotFound();
            var members = await access.GetMembersAsync(organizationId, ct);
            var userIds = await db.Viewers.Where(x => x.OrganizationId == organizationId && x.BotId == botId).Select(x => x.UserId).ToArrayAsync(ct);
            var active = members.Where(x => x.Role == "Viewer" && userIds.Contains(x.Id)).Select(x => new AccessRow(x.Email, false)).ToArray();
            var pending = await db.PendingAccess.Where(x => x.OrganizationId == organizationId && x.BotId == botId).Select(x => x.Email).ToArrayAsync(ct);
            return Results.Ok(active.Concat(pending.Where(email => !active.Any(x => string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase)))
                .Select(email => new AccessRow(email, true))).OrderBy(x => x.Email));
        });
        group.MapPost("/", async (Guid organizationId, Guid botId, AddAccess request, IOrganizationAccess access,IOrganizationInvitations invitations, BotsDbContext db, CancellationToken ct) =>
        {
            if (!await IsAdmin(organizationId, botId, access, db, ct)) return Results.NotFound();
            var email = request.Email?.Trim() ?? "";
            if (email.Length > 254 || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email) return Results.BadRequest();
            var normalized = email.ToUpperInvariant();
            var member = (await access.GetMembersAsync(organizationId, ct)).SingleOrDefault(x => string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase));
            if (member is not null && member.Role != "Viewer") return Results.Conflict(new { code = "admin_access" });
            var invitation=member is null?await invitations.EnsureViewerAsync(organizationId,email,request.Locale,ct):null;
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // Snapshot only bots existing during this operation. No grant is inherited by future bots.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM bots.bots WHERE organization_id = {organizationId} ORDER BY id FOR UPDATE", ct);
            var ids = request.AllBots ? await db.Bots.Where(x => x.OrganizationId == organizationId).Select(x => x.Id).ToArrayAsync(ct) : [botId];
            foreach (var id in ids)
            {
                if (member is not null)
                {
                    await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO bots.bot_viewers (organization_id, bot_id, user_id) VALUES ({organizationId}, {id}, {member.Id}) ON CONFLICT DO NOTHING", ct);
                    await db.PendingAccess.Where(x => x.OrganizationId == organizationId && x.BotId == id && x.Email == normalized).ExecuteDeleteAsync(ct);
                }
                else await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO bots.pending_access (organization_id, bot_id, email,invitation_id,invitation_generation) VALUES ({organizationId}, {id}, {normalized},{invitation!.Id},{invitation.Generation}) ON CONFLICT(organization_id,bot_id,email) DO UPDATE SET invitation_id=excluded.invitation_id,invitation_generation=excluded.invitation_generation", ct);
            }
            await transaction.CommitAsync(ct);
            return Results.Ok(new { Pending = member is null, AffectedBots = ids.Length });
        });
        group.MapPost("/resend",async(Guid organizationId,Guid botId,ResendAccess request,IOrganizationAccess access,IOrganizationInvitations invitations,BotsDbContext db,CancellationToken ct)=>{
            if(!await IsAdmin(organizationId,botId,access,db,ct))return Results.NotFound();var email=request.Email?.Trim().ToUpperInvariant()??"";
            if(!await db.PendingAccess.AnyAsync(p=>p.OrganizationId==organizationId&&p.BotId==botId&&p.Email==email,ct))return Results.NotFound();
            var invitation=await invitations.ResendViewerAsync(organizationId,email,request.Locale,ct);
            await db.PendingAccess.Where(p=>p.OrganizationId==organizationId&&p.Email==email).ExecuteUpdateAsync(setters=>setters.SetProperty(p=>p.InvitationId,invitation.Id).SetProperty(p=>p.InvitationGeneration,invitation.Generation),ct);return Results.NoContent();
        });
        group.MapDelete("/", async (Guid organizationId, Guid botId, string email, IOrganizationAccess access, BotsDbContext db, CancellationToken ct) =>
        {
            if (!await IsAdmin(organizationId, botId, access, db, ct)) return Results.NotFound();
            var member = (await access.GetMembersAsync(organizationId, ct)).SingleOrDefault(x => string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase));
            var normalized = email.ToUpperInvariant();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM bots.bots WHERE organization_id = {organizationId} AND id = {botId} FOR UPDATE", ct);
            if (member is not null) await db.Viewers.Where(x => x.OrganizationId == organizationId && x.BotId == botId && x.UserId == member.Id).ExecuteDeleteAsync(ct);
            await db.PendingAccess.Where(x => x.OrganizationId == organizationId && x.BotId == botId && x.Email == normalized).ExecuteDeleteAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.NoContent();
        });
    }
    private static async Task<bool> IsAdmin(Guid organizationId, Guid botId, IOrganizationAccess access, BotsDbContext db, CancellationToken ct) =>
        await access.HasAccessAsync(organizationId, true, ct) && await db.Bots.AnyAsync(x => x.OrganizationId == organizationId && x.Id == botId, ct);
    private sealed record AddAccess(string Email, bool AllBots,string Locale="en");
    private sealed record ResendAccess(string Email,string Locale="en");
    private sealed record AccessRow(string Email, bool Pending);
}
