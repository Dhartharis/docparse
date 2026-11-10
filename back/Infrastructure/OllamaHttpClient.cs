namespace DocParse.Infrastructure;

internal static class OllamaHttpClient
{
    public static void Configure(HttpClient client, IConfiguration configuration)
    {
        var endpoint = new Uri(configuration["Ollama:Endpoint"] ?? "http://ollama:11434/");
        if (!endpoint.IsAbsoluteUri || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0
            || endpoint.Scheme != "http" || !(endpoint.IsLoopback || endpoint.Host is "ollama" or "host.docker.internal"))
            throw new InvalidOperationException("Ollama requires an explicit local HTTP endpoint.");
        client.BaseAddress = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromMinutes(5);
    }
}
