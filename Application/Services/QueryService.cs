using DocParse.Infrastructure.MongoDB;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DocParse.Application.Services;

public class QueryService
{
    private readonly EmbeddingService _embeddingService;
    private readonly DocumentRepository _documentRepository;
    private readonly HttpClient _httpClient;

    public QueryService(EmbeddingService embeddingService, DocumentRepository documentRepository, HttpClient httpClient)
    {
        _embeddingService = embeddingService;
        _documentRepository = documentRepository;
        _httpClient = httpClient;
    }

    public async Task<string> QueryDocumentsAsync(string question, string apiKey)
    {
        // 1. Buscar documentos similares via pgvector
        var documentIds = await _embeddingService.SearchSimilarDocumentsAsync(question, apiKey);

        if (!documentIds.Any())
            return "No encontré documentos relevantes para responder tu pregunta.";

        // 2. Recuperar documentos completos desde MongoDB
        var documents = new List<string>();
        foreach (var id in documentIds)
        {
            var doc = await _documentRepository.GetByIdAsync(id);
            if (doc != null)
            {
                var docContent = JsonSerializer.Serialize(doc.Fields);
                    documents.Add($"Documento tipo {doc.Type}: {docContent}");
            }
        }

        // 3. Pasar contexto a GPT y obtener respuesta
        var context = string.Join("\n\n", documents);
        var prompt = $"""
            Tienes acceso a los siguientes documentos extraídos:
            
            {context}
            
            Responde la siguiente pregunta basándote únicamente en esos documentos:
            {question}
            """;

        return await AskGptAsync(prompt, apiKey);
    }

    private async Task<string> AskGptAsync(string prompt, string apiKey)
    {
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", apiKey);

        var payload = new
        {
            model = "gpt-4o-mini",
            messages = new[]
            {
                new { role = "user", content = prompt }
            }
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync("https://api.openai.com/v1/chat/completions", content);
        var responseBody = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(responseBody);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;
    }
}