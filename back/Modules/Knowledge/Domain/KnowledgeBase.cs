namespace DocParse.Modules.Knowledge.Domain;

public sealed class KnowledgeBase
{
    public DateTime? DeletedAt { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = "";
}
