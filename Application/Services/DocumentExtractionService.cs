using System.Text.Json;
using DocParse.Application.DTOs;
using DocParse.Domain.Models;
using DocParse.Infrastructure.MongoDB;
using DocParse.Infrastructure.OpenAI;

namespace DocParse.Application.Services;

public class DocumentExtractionService
{
    private readonly OpenAIClient _openAIClient;
    private readonly DocumentRepository _repository;
    private readonly EmbeddingService _embeddingService;
    private readonly IConfiguration _configuration;

    public DocumentExtractionService(
        OpenAIClient openAIClient,
        DocumentRepository repository,
        EmbeddingService embeddingService,
        IConfiguration configuration)
    {
        _openAIClient = openAIClient;
        _repository = repository;
        _embeddingService = embeddingService;
        _configuration = configuration;
    }

    public async Task<DocumentDTO> ExtractAsync(IFormFile file)
    {
        var mimeType = GetMimeType(file);
        var base64 = await ToBase64Async(file);

        var rawResponse = await _openAIClient.ExtractDocumentDataAsync(base64, mimeType);

        var dto = ParseResponse(rawResponse);

        // Guardar en MongoDB
        var document = new Document
        {
            Type = dto.DocumentType,
            Fields = dto.Fields,
            CreatedAt = DateTime.UtcNow
        };

        var saved = await _repository.SaveAsync(document);
        dto.Id = saved.Id;

        // Generar y guardar embedding en PostgreSQL
        var apiKey = _configuration["OpenAI:ApiKey"]!;
        var contentToEmbed = $"Tipo: {dto.DocumentType}. Campos: {string.Join(", ", dto.Fields.Select(f => $"{f.Key}: {f.Value}"))}";
        await _embeddingService.StoreEmbeddingAsync(saved.Id!, contentToEmbed, apiKey);

        return dto;
    }

    private DocumentDTO ParseResponse(string rawResponse)
    {
        try
        {
            var clean = rawResponse
                .Replace("```json", "")
                .Replace("```", "")
                .Trim();

            using var doc = JsonDocument.Parse(clean);
            var root = doc.RootElement;

            var dto = new DocumentDTO
            {
                DocumentType = root.GetProperty("type").GetString() ?? "Unknown"
            };

            foreach (var field in root.GetProperty("fields").EnumerateObject())
            {
                dto.Fields[field.Name] = field.Value.ToString();
            }

            return dto;
        }
        catch
        {
            return new DocumentDTO
            {
                DocumentType = "Unknown",
                Message = "Could not parse document response."
            };
        }
    }

    private string GetMimeType(IFormFile file)
    {
        var extension = Path.GetExtension(file.FileName).ToLower();
        return extension switch
        {
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => "application/octet-stream"
        };
    }

    private async Task<string> ToBase64Async(IFormFile file)
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        return Convert.ToBase64String(ms.ToArray());
    }
}