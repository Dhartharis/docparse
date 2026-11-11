namespace DocParse.Modules.Access.Contracts;

public sealed record OrganizationMember(Guid Id, string Email, string Role);

public interface IOrganizationAccess
{
    Guid UserId { get; }
    Task<string?> GetVerifiedEmailAsync(CancellationToken ct);
    Task<bool> HasAccessAsync(Guid organizationId, bool adminOnly = false, CancellationToken cancellationToken = default);
    Task<bool> AreViewersAsync(Guid organizationId, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OrganizationMember>> GetMembersAsync(Guid organizationId, CancellationToken cancellationToken = default);
}
