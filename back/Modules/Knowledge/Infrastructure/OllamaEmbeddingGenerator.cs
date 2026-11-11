using System.Net.Http.Json;
using System.Text.Json;
using DocParse.Modules.Knowledge.Application;
using DocParse.Modules.Usage.Contracts;

namespace DocParse.Modules.Knowledge.Infrastructure;

internal sealed class OllamaEmbeddingGenerator(HttpClient http, IConfiguration configuration,IUsageMeasurements metering) : IEmbeddingGenerator
{
    private string Model => configuration["Ollama:EmbeddingModel"] ?? "qwen3-embedding:0.6b";
    public string Profile => "ollama:" + Model + ":1024:v1";
    public async Task<float[]> GenerateAsync(string content, CancellationToken cancellationToken = default) =>
        (await GenerateManyAsync(["Instruct: Retrieve document passages that answer the question.\nQuery: " + content], cancellationToken))[0];

    public async Task<IReadOnlyList<float[]>> GenerateManyAsync(IReadOnlyList<string> content, CancellationToken cancellationToken = default)
    {
        var result = new List<float[]>();
        foreach (var batch in content.Chunk(32))
        {
            HttpResponseMessage response;
            try { response = await http.PostAsJsonAsync("api/embed", new { model = Model, input = batch, truncate = false, keep_alive = "5m" }, cancellationToken); }
            catch (HttpRequestException) { throw new DocumentProcessingUnavailable(true); }
            using (response)
            {
                if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.ServiceUnavailable) throw new DocumentProcessingUnavailable(true);
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                await metering.RecordAsync("ollama",Model,json.RootElement.TryGetProperty("prompt_eval_count",out var tokenCount)?tokenCount.GetInt64():null,0,cancellationToken);
                var vectors = json.RootElement.GetProperty("embeddings").EnumerateArray().Select(row => row.EnumerateArray().Select(x => x.GetSingle()).ToArray()).ToArray();
                if (vectors.Length != batch.Length || vectors.Any(row => row.Length != 1024 || row.Any(x => !float.IsFinite(x)) || row.All(x => x == 0))) throw new InvalidDataException("Invalid local embedding response.");
                result.AddRange(vectors);
            }
        }
        return result;
    }
}
