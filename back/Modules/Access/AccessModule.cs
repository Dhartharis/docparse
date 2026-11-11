using DocParse.Modules.Access.Contracts;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.EntityFrameworkCore;

namespace DocParse.Modules.Access;

public static class AccessModule
{
    public static IServiceCollection AddAccessModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<AccessDbContext>(options => options.UseNpgsql(configuration["PostgreSQL:ConnectionString"]));
        services.AddIdentityApiEndpoints<ApplicationUser>(options =>
        {
            options.User.RequireUniqueEmail = true;
            options.Password.RequiredLength = 12;
            options.SignIn.RequireConfirmedEmail = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        }).AddEntityFrameworkStores<AccessDbContext>();
        services.Configure<BearerTokenOptions>(IdentityConstants.BearerScheme, options =>
        {
            options.BearerTokenExpiration = TimeSpan.FromMinutes(15);
            options.RefreshTokenExpiration = TimeSpan.FromDays(7);
        });
        services.AddSingleton<MailOutboxStore>();
        services.AddSingleton<LocalEmailSender>();
        services.AddHostedService<MailOutboxWorker>();
        services.AddScoped<InvitationAdministration>();
        services.AddScoped<IOrganizationInvitations>(p=>p.GetRequiredService<InvitationAdministration>());
        services.AddSingleton<IEmailSender<ApplicationUser>>(provider => provider.GetRequiredService<LocalEmailSender>());
        services.AddHttpContextAccessor();
        services.AddScoped<IOrganizationAccess, OrganizationAccess>();
        services.AddScoped<MembershipAdministration>();
        services.AddScoped<IAuthorizationHandler, CurrentIdentityHandler>();
        services.AddAuthorization(options => options.DefaultPolicy = new AuthorizationPolicyBuilder(IdentityConstants.BearerScheme)
            .RequireAuthenticatedUser().AddRequirements(new CurrentIdentityRequirement()).Build());
        return services;
    }

    public static void MapAccessModule(this WebApplication app)
    {
        app.MapOwnerOnboarding();
        var auth = app.MapGroup("/api/auth").RequireRateLimiting("authentication");
        auth.AddEndpointFilter(async (context, next) =>
        {
            if (context.Arguments.OfType<RegisterRequest>().Any())
                return Results.Problem(statusCode: 403, title: "Registration requires an invitation.", extensions: new Dictionary<string, object?> { ["code"] = "invitation_required" });
            var login = context.Arguments.OfType<LoginRequest>().SingleOrDefault();
            if (login is not null && login.Email.Contains('@'))
            {
                var users = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.FindByEmailAsync(login.Email);
                if (user is null) return Results.Unauthorized();
                context.Arguments[context.Arguments.IndexOf(login)] = new LoginRequest
                {
                    Email = user.UserName!, Password = login.Password,
                    TwoFactorCode = login.TwoFactorCode, TwoFactorRecoveryCode = login.TwoFactorRecoveryCode
                };
            }
            if (context.HttpContext.Request.Query["useCookies"].Any(x => string.Equals(x, "true", StringComparison.OrdinalIgnoreCase))
                || context.HttpContext.Request.Query["useSessionCookies"].Any(x => string.Equals(x, "true", StringComparison.OrdinalIgnoreCase)))
                return Results.Problem(statusCode: 400, title: "Use bearer authentication for this API.");
            var result=await next(context);
            if(context.HttpContext.Request.Path.Value?.EndsWith("/confirmEmail")==true&&context.HttpContext.Request.Headers.Accept.ToString().Contains("text/html")){
                while(result is Microsoft.AspNetCore.Http.INestedHttpResult nested)result=nested.Result;
                var status=(result as Microsoft.AspNetCore.Http.IStatusCodeHttpResult)?.StatusCode??200;
                var site=app.Configuration["PublicWeb:BaseUrl"]??"http://127.0.0.1:5173";
                return Results.Redirect(site.TrimEnd('/')+"/#confirmation="+(status is >=200 and <300?"success":"failed"));
            }
            return result;
        });
        auth.MapIdentityApi<ApplicationUser>();
        auth.MapPost("/logout-all", async (UserManager<ApplicationUser> users, System.Security.Claims.ClaimsPrincipal principal) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null) return Results.Unauthorized();
            await users.UpdateSecurityStampAsync(user);
            return Results.NoContent();
        }).RequireAuthorization();
        auth.MapGet("/me", async (UserManager<ApplicationUser> users, System.Security.Claims.ClaimsPrincipal principal) =>
        {
            var user = await users.GetUserAsync(principal);
            return user is null ? Results.Unauthorized() : Results.Ok(new { user.Id, user.Email, user.UserName });
        }).RequireAuthorization();

        var organizations = app.MapGroup("/api/organizations").RequireAuthorization();
        organizations.MapGet("/", async (AccessDbContext db, IOrganizationAccess access, CancellationToken ct) =>
            await (from membership in db.Memberships
                   join organization in db.Organizations on membership.OrganizationId equals organization.Id
                   where membership.UserId == access.UserId
                   select new { organization.Id, organization.Name, membership.Role }).ToListAsync(ct));

        organizations.MapPost("/", async (CreateOrganization request, AccessDbContext db, IOrganizationAccess access, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["A name of 1–200 characters is required."] });
            if(await access.GetVerifiedEmailAsync(ct)is null)return Results.Forbid();
            if(request.RequestId is Guid replayId){var existing=await db.Organizations.SingleOrDefaultAsync(o=>o.CreatedByUserId==access.UserId&&o.CreationRequestId==replayId,ct);if(existing is not null)return Results.Created($"/api/organizations/{existing.Id}",new{existing.Id,existing.Name,Role="Admin"});}
            var organization = new Organization { Name = request.Name.Trim(),CreatedByUserId=access.UserId,CreationRequestId=request.RequestId };
            db.Organizations.Add(organization);
            db.Memberships.Add(new Membership { OrganizationId = organization.Id, UserId = access.UserId, Role = "Admin" });
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/organizations/{organization.Id}", new { organization.Id, organization.Name, Role = "Admin" });
        });

        organizations.MapGet("/{organizationId:guid}/members", async (Guid organizationId, AccessDbContext db, IOrganizationAccess access, CancellationToken ct) =>
        {
            if (!await access.HasAccessAsync(organizationId, true, ct)) return Results.NotFound();
            var members = await (from member in db.Memberships
                                 join user in db.Users on member.UserId equals user.Id
                                 where member.OrganizationId == organizationId
                                 select new { user.Id, user.Email, member.Role }).ToListAsync(ct);
            return Results.Ok(members);
        });
        app.MapInvitations();
        organizations.MapPatch("/{organizationId:guid}/members/{userId:guid}", async (Guid organizationId, Guid userId,
            ChangeRole request, IOrganizationAccess access, MembershipAdministration administration, CancellationToken ct) =>
                MapMembershipChange(await administration.ChangeAsync(access.UserId, organizationId, userId, request.Role ?? "", ct)));
        organizations.MapDelete("/{organizationId:guid}/members/{userId:guid}", async (Guid organizationId, Guid userId,
            IOrganizationAccess access, MembershipAdministration administration, CancellationToken ct) =>
                MapMembershipChange(await administration.ChangeAsync(access.UserId, organizationId, userId, null, ct)));
    }

    public sealed record CreateOrganization(string Name,Guid? RequestId=null);
    public sealed record ChangeRole(string Role);
    private static IResult MapMembershipChange(MembershipChange change) => change switch
    {
        MembershipChange.Success => Results.NoContent(),
        MembershipChange.LastAdmin => Results.Conflict(new { code = "last_admin" }),
        MembershipChange.InvalidRole => Results.ValidationProblem(new Dictionary<string, string[]> { ["role"] = ["Choose Admin or Viewer."] }),
        _ => Results.NotFound()
    };
}
