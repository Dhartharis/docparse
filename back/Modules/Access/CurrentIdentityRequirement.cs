using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace DocParse.Modules.Access;

internal sealed class CurrentIdentityRequirement : IAuthorizationRequirement { }

internal sealed class CurrentIdentityHandler(UserManager<ApplicationUser> users, IOptions<IdentityOptions> options)
    : AuthorizationHandler<CurrentIdentityRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, CurrentIdentityRequirement requirement)
    {
        var user = await users.GetUserAsync(context.User);
        var stamp = context.User.FindFirst(options.Value.ClaimsIdentity.SecurityStampClaimType)?.Value;
        if (user is not null && stamp is not null && user.SecurityStamp == stamp && user.EmailConfirmed
            && (!user.LockoutEnd.HasValue || user.LockoutEnd <= DateTimeOffset.UtcNow))
            context.Succeed(requirement);
    }
}
