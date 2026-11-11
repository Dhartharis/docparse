using Microsoft.EntityFrameworkCore;

namespace DocParse.Modules.Access;

public enum MembershipChange { Success, NotFound, LastAdmin, InvalidRole }

public sealed class MembershipAdministration(AccessDbContext database)
{
    public async Task<MembershipChange> ChangeAsync(Guid actorId, Guid organizationId, Guid userId, string? role, CancellationToken cancellationToken)
    {
        if (role is not (null or "Admin" or "Viewer")) return MembershipChange.InvalidRole;
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        // Serialize all membership removals/demotions within this organization.
        await database.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM access.organizations WHERE \"Id\" = {organizationId} FOR UPDATE", cancellationToken);
        if (!await database.Memberships.AnyAsync(x => x.OrganizationId == organizationId && x.UserId == actorId && x.Role == "Admin", cancellationToken))
            return MembershipChange.NotFound;
        var membership = await database.Memberships.SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.UserId == userId, cancellationToken);
        if (membership is null) return MembershipChange.NotFound;
        if (membership.Role == "Admin" && role != "Admin"
            && await database.Memberships.CountAsync(x => x.OrganizationId == organizationId && x.Role == "Admin", cancellationToken) <= 1)
            return MembershipChange.LastAdmin;
        if (role is null) database.Memberships.Remove(membership);
        else membership.Role = role;
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return MembershipChange.Success;
    }
}
