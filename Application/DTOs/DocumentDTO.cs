namespace DocParse.Application.DTOs;

public class DocumentDTO
{
    public string? Id { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public Dictionary<string, string> Fields { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}