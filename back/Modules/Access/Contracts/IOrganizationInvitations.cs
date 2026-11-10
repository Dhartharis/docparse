namespace DocParse.Modules.Access.Contracts;

public sealed record InvitationReceipt(Guid Id,Guid Generation,DateTime ExpiresAt);
public interface IOrganizationInvitations {
 Task<InvitationReceipt> EnsureViewerAsync(Guid organizationId,string email,string locale,CancellationToken ct);
 Task<InvitationReceipt> ResendViewerAsync(Guid organizationId,string email,string locale,CancellationToken ct);
 Task<IReadOnlyCollection<Guid>> GetAcceptedGenerationsAsync(Guid organizationId,CancellationToken ct);
}
