using DocParse.Modules.Access.Contracts;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace DocParse.Modules.Access;

internal sealed class OrganizationAccess(AccessDbContext database, IHttpContextAccessor http) : IOrganizationAccess
{
    public Guid UserId => Guid.TryParse(http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        ? id : throw new UnauthorizedAccessException();

    public Task<string?> GetVerifiedEmailAsync(CancellationToken ct)=>database.Users.Where(u=>u.Id==UserId&&u.EmailConfirmed).Select(u=>u.NormalizedEmail).SingleOrDefaultAsync(ct);
    public Task<bool> HasAccessAsync(Guid organizationId, bool adminOnly = false, CancellationToken cancellationToken = default)
    {
        var userId = UserId;
        return database.Memberships.AnyAsync(x => x.OrganizationId == organizationId && x.UserId == userId
            && (!adminOnly || x.Role == "Admin"), cancellationToken);
    }

    public async Task<bool> AreViewersAsync(Guid organizationId, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
    {
        var ids = userIds.Distinct().ToArray();
        return await database.Memberships.CountAsync(x => x.OrganizationId == organizationId && x.Role == "Viewer"
            && ids.Contains(x.UserId), cancellationToken) == ids.Length;
    }
    public async Task<IReadOnlyList<OrganizationMember>> GetMembersAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        if (!await HasAccessAsync(organizationId, true, cancellationToken)) return [];
        return await (from member in database.Memberships join user in database.Users on member.UserId equals user.Id
                      where member.OrganizationId == organizationId
                      select new OrganizationMember(user.Id, user.Email!, member.Role)).ToListAsync(cancellationToken);
    }
}
