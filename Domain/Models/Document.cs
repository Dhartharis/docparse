using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace DocParse.Domain.Models;

public class Document
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    public string Type { get; set; } = string.Empty;
    public Dictionary<string, string> Fields { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}