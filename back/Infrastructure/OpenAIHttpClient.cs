namespace DocParse.Infrastructure;

internal static class OpenAIHttpClient
{
    public static void Configure(HttpClient client, IConfiguration configuration, IHostEnvironment environment)
    {
        var endpoint = new Uri(configuration["OpenAI:Endpoint"] ?? "https://api.openai.com/v1/");
        if (!endpoint.IsAbsoluteUri || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0
            || (!environment.IsDevelopment() && endpoint.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("An absolute HTTPS OpenAI endpoint is required outside development.");
        client.BaseAddress = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromMinutes(2);
    }
}
