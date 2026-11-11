using DocParse.Modules.Chat.Application;
using DocParse.Modules.Chat.Infrastructure;

namespace DocParse.Modules.Chat;

public static class ChatModule
{
    public static IServiceCollection AddChatModule(this IServiceCollection services, IConfiguration configuration)
    {
        if (configuration["AI:Provider"] == "Ollama") services.AddHttpClient<IAnswerGenerator, OllamaAnswerGenerator>(client => DocParse.Infrastructure.OllamaHttpClient.Configure(client, configuration));
        else services.AddHttpClient<IAnswerGenerator, OpenAIAnswerGenerator>((provider, client) =>
            DocParse.Infrastructure.OpenAIHttpClient.Configure(client, configuration, provider.GetRequiredService<IHostEnvironment>()));
        services.AddScoped<QueryService>();
        return services;
    }
}
