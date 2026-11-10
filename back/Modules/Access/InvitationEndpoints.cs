using DocParse.Modules.Access.Contracts;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DocParse.Modules.Access;

internal static class InvitationEndpoints
{
    public static void MapInvitations(this WebApplication app)
    {
        app.MapPost("/api/organizations/{organizationId:guid}/invitations", async (Guid organizationId,InviteMember request,InvitationAdministration administration,CancellationToken ct)=>{
            var result=await administration.InviteAsync(organizationId,request.Email,request.Role,request.Locale,"team",true,ct);
            return Results.Created($"/api/organizations/{organizationId}/invitations/{result.Id}",result);
        }).RequireAuthorization();
        app.MapGet("/api/organizations/{organizationId:guid}/invitations",async(Guid organizationId,IOrganizationAccess access,AccessDbContext db,CancellationToken ct)=>{
            if(!await access.HasAccessAsync(organizationId,true,ct))return Results.NotFound();
            return Results.Ok(await db.Invitations.Where(i=>i.OrganizationId==organizationId&&i.AcceptedAt==null&&i.RevokedAt==null&&i.ExpiresAt>DateTime.UtcNow).Select(i=>new{i.Id,i.Email,i.Role,i.ExpiresAt,delivery=db.MailOutbox.Where(m=>m.InvitationId==i.Id&&m.Generation==i.Generation).OrderByDescending(m=>m.CreatedAt).Select(m=>m.State).FirstOrDefault()}).ToArrayAsync(ct));
        }).RequireAuthorization();
        app.MapDelete("/api/organizations/{organizationId:guid}/invitations/{invitationId:guid}",async(Guid organizationId,Guid invitationId,IOrganizationAccess access,AccessDbContext db,CancellationToken ct)=>{
            if(!await access.HasAccessAsync(organizationId,true,ct))return Results.NotFound();
            var count=await db.Invitations.Where(i=>i.OrganizationId==organizationId&&i.Id==invitationId&&i.AcceptedAt==null).ExecuteUpdateAsync(setters=>setters.SetProperty(i=>i.RevokedAt,DateTime.UtcNow).SetProperty(i=>i.TokenCiphertext,(string?)null),ct);return count==1?Results.NoContent():Results.NotFound();
        }).RequireAuthorization();
        app.MapPost("/api/invitations/preview",async(PreviewInvitation request,AccessDbContext db,CancellationToken ct)=>{
            if(string.IsNullOrWhiteSpace(request.Token)||request.Token.Length!=64)return InvalidInvitation();var hash=Hash(request.Token);
            var invitation=await db.Invitations.AsNoTracking().SingleOrDefaultAsync(i=>i.TokenHash==hash&&i.AcceptedAt==null&&i.RevokedAt==null&&i.ExpiresAt>DateTime.UtcNow,ct);if(invitation is null)return InvalidInvitation();
            var organization=await db.Organizations.Where(o=>o.Id==invitation.OrganizationId).Select(o=>o.Name).SingleAsync(ct);
            return Results.Ok(new{invitation.Email,invitation.Role,invitation.OrganizationId,organization,invitation.ExpiresAt,invitation.Locale,existingAccount=await db.Users.AnyAsync(u=>u.NormalizedEmail==invitation.Email,ct)});
        }).RequireRateLimiting("authentication");

        app.MapPost("/api/invitations/register", async (RegisterInvitation request, AccessDbContext db,
            UserManager<ApplicationUser> users, LocalEmailSender mail, IConfiguration configuration, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Token) || request.Token.Length != 64)
                return InvalidInvitation();
            if(string.IsNullOrWhiteSpace(request.Password))return Results.ValidationProblem(new Dictionary<string,string[]>{{"PasswordTooShort",["A password is required."]}});
            if (string.IsNullOrWhiteSpace(request.UserName) || request.UserName.Length > 64
                || request.UserName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["userName"] = ["Use 1–64 letters, numbers, dots, underscores or hyphens."] });
            var hash = Hash(request.Token);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var invitation = await db.Invitations.FromSqlInterpolated($"SELECT * FROM access.invitations WHERE \"TokenHash\" = {hash} FOR UPDATE").SingleOrDefaultAsync(ct);
            if (invitation is null || invitation.AcceptedAt is not null || invitation.RevokedAt is not null || invitation.ExpiresAt <= DateTime.UtcNow
                || !string.Equals(invitation.Email, request.Email, StringComparison.OrdinalIgnoreCase)) return InvalidInvitation();
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = request.UserName, Email = request.Email,Locale=invitation.Locale };
            var created = await users.CreateAsync(user, request.Password);
            if (!created.Succeeded)
                return Results.ValidationProblem(created.Errors.GroupBy(x => x.Code).ToDictionary(x => x.Key, x => x.Select(e => e.Description).ToArray()));
            db.Memberships.Add(new Membership { OrganizationId = invitation.OrganizationId, UserId = user.Id, Role = invitation.Role });
            invitation.AcceptedAt = DateTime.UtcNow;
            invitation.AcceptedUserId=user.Id;
            invitation.TokenCiphertext=null;
            // Queue confirmation atomically with account and membership; SMTP runs after commit.
            var code = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await users.GenerateEmailConfirmationTokenAsync(user)));
            var path = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString("/api/auth/confirmEmail", new Dictionary<string, string?> { ["userId"] = user.Id.ToString(), ["code"] = code });
            var link = new Uri(new Uri(configuration["PublicApi:BaseUrl"] ?? "http://127.0.0.1:5128"), path).AbsoluteUri;
            db.MailOutbox.Add(mail.PrepareConfirmation(user,user.Email!,link));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok();
        }).RequireRateLimiting("authentication");

        app.MapPost("/api/invitations/accept", async (AcceptInvitation request, AccessDbContext db, IOrganizationAccess access,
            UserManager<ApplicationUser> users, System.Security.Claims.ClaimsPrincipal principal, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Token) || request.Token.Length != 64) return Results.NotFound();
            var user = await users.GetUserAsync(principal);
            if (user is null || !user.EmailConfirmed) return Results.NotFound();
            var hash = Hash(request.Token);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var invitation = await db.Invitations.FromSqlInterpolated($"SELECT * FROM access.invitations WHERE \"TokenHash\" = {hash} FOR UPDATE").SingleOrDefaultAsync(ct);
            if (invitation is null || invitation.Email != user.NormalizedEmail || invitation.RevokedAt is not null || invitation.ExpiresAt <= DateTime.UtcNow)
                return Results.NotFound();
            if(invitation.AcceptedAt is not null&&(invitation.AcceptedUserId!=user.Id||!await db.Memberships.AnyAsync(m=>m.OrganizationId==invitation.OrganizationId&&m.UserId==user.Id,ct)))return Results.NotFound();
            if (invitation.AcceptedAt is null)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO access.memberships (\"OrganizationId\", \"UserId\", \"Role\") VALUES ({invitation.OrganizationId}, {access.UserId}, {invitation.Role}) ON CONFLICT DO NOTHING", ct);
                invitation.AcceptedAt = DateTime.UtcNow;
            invitation.AcceptedUserId=user.Id;
            invitation.TokenCiphertext=null;
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization();
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static IResult InvalidInvitation() => Results.Problem(statusCode: 400, title: "A valid invitation for this email is required.", extensions: new Dictionary<string, object?> { ["code"] = "invitation_required" });
    internal sealed record InviteMember(string Email, string Role,string Locale="en");
    internal sealed record PreviewInvitation(string Token);
    internal sealed record AcceptInvitation(string Token);
    internal sealed record RegisterInvitation(string Email, string UserName, string Password, string Token);
}
