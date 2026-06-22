using MongoDB.Driver;
using DocParse.Domain.Models;

namespace DocParse.Infrastructure.MongoDB;

public class DocumentRepository
{
    private readonly IMongoCollection<Document> _collection;

    public DocumentRepository(MongoDbContext context, IConfiguration configuration)
    {
        var collectionName = configuration["MongoDB:CollectionName"] ?? "documents";
        _collection = context.Documents(collectionName);
    }

    public async Task<Document> SaveAsync(Document document)
    {
        await _collection.InsertOneAsync(document);
        return document;
    }

    public async Task<Document?> GetByIdAsync(string id)
    {
        return await _collection.Find(d => d.Id == id).FirstOrDefaultAsync();
    }

    public async Task<List<Document>> GetAllAsync()
    {
        return await _collection.Find(_ => true).ToListAsync();
    }
}