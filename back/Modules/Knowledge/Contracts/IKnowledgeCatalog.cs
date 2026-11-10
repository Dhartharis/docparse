namespace DocParse.Modules.Knowledge.Contracts;

public record KnowledgeSource(Guid DocumentId, string Content, string Title, int? PageNumber = null);

public interface IKnowledgeCatalog
{
    Task<bool> ContainsAsync(Guid organizationId, IReadOnlyCollection<Guid> knowledgeBaseIds, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<KnowledgeSource>> SearchAsync(Guid organizationId, IReadOnlyCollection<Guid> knowledgeBaseIds, string question, CancellationToken cancellationToken = default);
}
