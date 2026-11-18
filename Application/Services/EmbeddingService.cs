using DocParse.Domain.Models;
using DocParse.Infrastructure.PostgreSQL;
using Pgvector;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Pgvector.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DocParse.Application.Services;

public class EmbeddingService
{
    private readonly HttpClient _httpClient;
    private readonly VectorDbContext _vectorDbContext;

    public EmbeddingService(HttpClient httpClient, VectorDbContext vectorDbContext)
    {
        _httpClient = httpClient;
        _vectorDbContext = vectorDbContext;
    }

    public async Task StoreEmbeddingAsync(string documentId, string content, string apiKey)
    {
        var embedding = await GenerateEmbeddingAsync(content, apiKey);

        var documentEmbedding = new DocumentEmbedding
        {
            Id = Guid.NewGuid(),
            DocumentId = documentId,
            Content = content,
            Embedding = new Vector(embedding)
        };

        _vectorDbContext.DocumentEmbeddings.Add(documentEmbedding);
        await _vectorDbContext.SaveChangesAsync();
    }

    public async Task<List<string>> SearchSimilarDocumentsAsync(string query, string apiKey, int topK = 3)
    {
        var queryEmbedding = await GenerateEmbeddingAsync(query, apiKey);
        var vector = new Vector(queryEmbedding);

        Console.WriteLine($"Query vector generado, dimensiones: {queryEmbedding.Length}");
        Console.WriteLine($"Total embeddings en DB: {await _vectorDbContext.DocumentEmbeddings.CountAsync()}");

        var similar = await _vectorDbContext.DocumentEmbeddings
            .OrderBy(e => e.Embedding!.CosineDistance(vector))
            .Take(topK)
            .ToListAsync();

        Console.WriteLine($"Documentos encontrados: {similar.Count}");
        foreach (var doc in similar)
        {
            Console.WriteLine($"DocumentId: {doc.DocumentId}, Content: {doc.Content[..50]}");
        }

        return similar.Select(e => e.DocumentId).ToList();
    }

    private async Task<float[]> GenerateEmbeddingAsync(string text, string apiKey)
    {
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", apiKey);

        var payload = new
        {
            input = text,
            model = "text-embedding-3-small"
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync("https://api.openai.com/v1/embeddings", content);
        var responseBody = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(responseBody);
        var embeddingArray = doc.RootElement
            .GetProperty("data")[0]
            .GetProperty("embedding")
            .EnumerateArray()
            .Select(e => e.GetSingle())
            .ToArray();

        return embeddingArray;
    }
}