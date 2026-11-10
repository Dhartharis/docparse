using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DocParse.Modules.Knowledge.Application;

namespace DocParse.Modules.Knowledge.Infrastructure;

public class OpenAIClient : IDocumentExtractor
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public OpenAIClient(HttpClient httpClient, IConfiguration configuration)
    {
        _apiKey = configuration["OpenAI:ApiKey"] ?? string.Empty;
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    public async Task<string> ExtractDocumentDataAsync(string base64File, string mimeType, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            throw new DocumentProcessingUnavailable();

        object fileContent;

        if (mimeType == "application/pdf")
        {
            fileContent = new
            {
                type = "file",
                file = new
                {
                    filename = "document.pdf",
                    file_data = $"data:application/pdf;base64,{base64File}"
                }
            };
        }
        else
        {
            fileContent = new
            {
                type = "image_url",
                image_url = new { url = $"data:{mimeType};base64,{base64File}" }
            };
        }

        var payload = new
        {
            model = "gpt-4o-mini",
            max_tokens = 1000,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        fileContent,
                        new
                        {
                            type = "text",
                            text = """
                                Analyze this document and respond ONLY with a JSON object. No explanation, no markdown.
                                
                                Determine the document type: Invoice, Receipt, Contract, or Unknown.
                                
                                Then extract the relevant fields based on type:
                                - Invoice: supplier, date, invoice_number, items, subtotal, tax, total
                                - Receipt: store, date, items, total
                                - Contract: parties, start_date, end_date, key_amounts
                                - Unknown: any relevant fields you can identify
                                
                                Response format:
                                {
                                  "type": "Invoice",
                                  "fields": {
                                    "supplier": "...",
                                    "date": "...",
                                    "total": "..."
                                  }
                                }
                                """
                        }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.PostAsync("chat/completions", content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(responseBody);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;
    }
}
