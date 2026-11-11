namespace DocParse.Modules.Knowledge.Contracts;

public class DocumentDTO
{
    public bool Deduplicated { get; set; }
    public string ProcessingStatus { get; set; } = "ready";
    public string? Id { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public Dictionary<string, string> Fields { get; set; } = new();
    public IReadOnlyList<DocumentPage> Pages { get; set; } = [];
    public string Message { get; set; } = string.Empty;
}

public sealed record DocumentPage(int Number, string Text);
