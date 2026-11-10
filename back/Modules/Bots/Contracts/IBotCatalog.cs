namespace DocParse.Modules.Bots.Contracts;

public sealed record ChatBot(Guid Id, Guid OrganizationId, IReadOnlyCollection<Guid> KnowledgeBaseIds);
public interface IBotCatalog
{
    Task<ChatBot?> GetForCurrentUserAsync(Guid organizationId, Guid botId, CancellationToken cancellationToken = default);
}
