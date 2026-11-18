using MongoDB.Driver;
using MongoDB.Driver.Core.Configuration;
using DocParse.Domain.Models;

namespace DocParse.Infrastructure.MongoDB;

public class MongoDbContext
{
    private readonly IMongoDatabase _database;

    public MongoDbContext(IConfiguration configuration)
    {
        var connectionString = configuration["MongoDB:ConnectionString"]
            ?? throw new Exception("MongoDB connection string not configured");
        var databaseName = configuration["MongoDB:DatabaseName"]
            ?? throw new Exception("MongoDB database name not configured");

        var settings = MongoClientSettings.FromConnectionString(connectionString);
        settings.SslSettings = new SslSettings
        {
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12
        };

        var client = new MongoClient(settings);
        _database = client.GetDatabase(databaseName);
    }

    public IMongoCollection<Document> Documents(string collectionName) =>
        _database.GetCollection<Document>(collectionName);
}