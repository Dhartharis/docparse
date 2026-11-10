using DocParse.Modules.Access.Contracts;

using DocParse.Modules.Knowledge.Application;
using DocParse.Modules.Knowledge.Contracts;
using DocParse.Modules.Knowledge.Domain;
using DocParse.Modules.Knowledge.Endpoints;
using DocParse.Modules.Knowledge.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace DocParse.Modules.Knowledge;

public static class KnowledgeModule
{
    public static IServiceCollection AddKnowledgeModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<KnowledgeDbContext>(options => options.UseNpgsql(configuration["PostgreSQL:ConnectionString"], x => x.UseVector()));
        services.AddScoped<IDocumentStore, DocumentRepository>();
        services.AddSingleton<DocumentFileStorage>();
        services.AddSingleton<IOriginalFileStorage>(p=>p.GetRequiredService<DocumentFileStorage>());
        services.AddHostedService<KnowledgeRetentionWorker>();
        if (configuration["AI:Provider"] == "Ollama")
        {
            services.AddScoped<IDocumentExtractor, LocalDocumentExtractor>();
            services.AddHttpClient<IEmbeddingGenerator, OllamaEmbeddingGenerator>(http => DocParse.Infrastructure.OllamaHttpClient.Configure(http, configuration));
        }
        else
        {
            services.AddHttpClient<IDocumentExtractor, OpenAIClient>((provider, client) => DocParse.Infrastructure.OpenAIHttpClient.Configure(client, configuration, provider.GetRequiredService<IHostEnvironment>()));
            services.AddHttpClient<IEmbeddingGenerator, EmbeddingService>((provider, client) => DocParse.Infrastructure.OpenAIHttpClient.Configure(client, configuration, provider.GetRequiredService<IHostEnvironment>()));
        }
        services.AddScoped<DocumentExtractionService>();
        services.AddHostedService<DocumentProcessingWorker>();
        services.AddScoped<IDocumentExporter, ExportService>();
        services.AddScoped<IKnowledgeCatalog, KnowledgeCatalog>();
        services.AddScoped<KnowledgeAdminFilter>();
        return services;
    }

    public static void MapKnowledgeModule(this WebApplication app)
    {
        KnowledgeTrashEndpoints.MapKnowledgeTrash(app);
        var group = app.MapGroup("/api/organizations/{organizationId:guid}/knowledge-bases").RequireAuthorization();
        group.MapGet("/", async (Guid organizationId, IOrganizationAccess access, KnowledgeDbContext db, CancellationToken ct) =>
        {
            if (!await access.HasAccessAsync(organizationId, true, ct)) return Results.NotFound();
            return Results.Ok(await db.KnowledgeBases.Where(x => x.OrganizationId == organizationId).Select(x => new { x.Id, x.Name }).ToListAsync(ct));
        });
        group.MapPatch("/{knowledgeBaseId:guid}", async (Guid organizationId, Guid knowledgeBaseId, CreateKnowledgeBase request,
            IOrganizationAccess access, KnowledgeDbContext db, CancellationToken ct) =>
        {
            if (!await access.HasAccessAsync(organizationId, true, ct)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200) return Results.BadRequest();
            var knowledge = await db.KnowledgeBases.SingleOrDefaultAsync(x => x.Id == knowledgeBaseId && x.OrganizationId == organizationId, ct);
            if (knowledge is null) return Results.NotFound();
            knowledge.Name = request.Name.Trim();
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
        group.MapPost("/", async (Guid organizationId, CreateKnowledgeBase request, IOrganizationAccess access, KnowledgeDbContext db, CancellationToken ct) =>
        {
            if (!await access.HasAccessAsync(organizationId, true, ct)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200) return Results.BadRequest();
            var knowledge = new KnowledgeBase { OrganizationId = organizationId, Name = request.Name.Trim() };
            db.KnowledgeBases.Add(knowledge);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/organizations/{organizationId}/knowledge-bases/{knowledge.Id}", new { knowledge.Id, knowledge.Name });
        });
    }
    public sealed record CreateKnowledgeBase(string Name);
}
