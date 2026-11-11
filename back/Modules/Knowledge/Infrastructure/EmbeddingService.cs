using DocParse.Modules.Knowledge.Domain;
using DocParse.Modules.Knowledge.Infrastructure;
using Pgvector;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Pgvector.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using DocParse.Modules.Knowledge.Contracts;
using DocParse.Modules.Knowledge.Application;

namespace DocParse.Modules.Knowledge.Infrastructure;

public class EmbeddingService : IEmbeddingGenerator
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    public EmbeddingService(HttpClient httpClient, IConfiguration configuration) { _httpClient = httpClient; _configuration = configuration; }

    public async Task<float[]> GenerateAsync(string text, CancellationToken cancellationToken = default)
    {
        var apiKey = _configuration["OpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Configure OpenAI:ApiKey before using document extraction or search.");

        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", apiKey);

        var payload = new
        {
            input = text,
            model = "text-embedding-3-small"
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.PostAsync("embeddings", content, cancellationToken);
        response.EnsureSuccessStatusCode();
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        using var doc = JsonDocument.Parse(responseBody);
        var embeddingArray = doc.RootElement
            .GetProperty("data")[0]
            .GetProperty("embedding")
            .EnumerateArray()
            .Select(e => e.GetSingle())
            .ToArray();

        if (embeddingArray.Length != 1536 || embeddingArray.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("Embedding provider returned an invalid vector.");
        return embeddingArray;
    }
}
