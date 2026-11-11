using DocParse.Modules.Bots.Contracts;
using DocParse.Modules.Access.Contracts;

using DocParse.Modules.Knowledge.Contracts;
using Microsoft.EntityFrameworkCore;

namespace DocParse.Modules.Bots;

internal sealed class BotCatalog(BotsDbContext database, IOrganizationAccess access,Infrastructure.BotInvitationActivation activation) : IBotCatalog
{
    public async Task<ChatBot?> GetForCurrentUserAsync(Guid organizationId, Guid botId, CancellationToken cancellationToken = default)
    {
        if (!await access.HasAccessAsync(organizationId, cancellationToken: cancellationToken)) return null;
        await activation.ApplyAsync(organizationId,cancellationToken);
        if (!await access.HasAccessAsync(organizationId, true, cancellationToken)
            && !await database.Viewers.AnyAsync(x => x.OrganizationId == organizationId && x.BotId == botId
                && x.UserId == access.UserId, cancellationToken)) return null;
        if (!await database.Bots.AnyAsync(x => x.Id == botId && x.OrganizationId == organizationId, cancellationToken)) return null;
        var ids = await database.KnowledgeBases.Where(x => x.OrganizationId == organizationId && x.BotId == botId)
            .Select(x => x.KnowledgeBaseId).ToArrayAsync(cancellationToken);
        return new ChatBot(botId, organizationId, ids);
    }
}

public static class BotsModule
{
    public static IServiceCollection AddBotsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<BotsDbContext>(options => options.UseNpgsql(configuration["PostgreSQL:ConnectionString"]));
        services.AddScoped<IBotCatalog, BotCatalog>();
        services.AddScoped<Infrastructure.BotInvitationActivation>();
        services.AddScoped<Infrastructure.BotChannelStore>();
        services.AddScoped<IBotChannelAccess>(p=>p.GetRequiredService<Infrastructure.BotChannelStore>());
        services.AddSingleton<Infrastructure.BotLogoStorage>();
        services.AddHostedService<Infrastructure.BotRetentionWorker>();
        return services;
    }

    public static void MapBotsModule(this WebApplication app)
    {
        Endpoints.BotAccessEndpoints.MapBotAccess(app);
        Endpoints.BotChannelEndpoints.MapBotChannels(app);
        Endpoints.WidgetEndpoints.MapWidget(app);
        Endpoints.BotTrashEndpoints.MapBotTrash(app);
        var group = app.MapGroup("/api/organizations/{organizationId:guid}/bots").RequireAuthorization();
        group.MapGet("/", async (Guid organizationId, IOrganizationAccess access, BotsDbContext db, Infrastructure.BotInvitationActivation activation, CancellationToken ct) =>
        {
            await activation.ApplyAsync(organizationId,ct);
            if (!await access.HasAccessAsync(organizationId, cancellationToken: ct)) return Results.NotFound();
            var admin = await access.HasAccessAsync(organizationId, true, ct);
            var userId = access.UserId;
            return Results.Ok(await db.Bots.Where(x => x.OrganizationId == organizationId
                && (admin || db.Viewers.Any(viewer => viewer.OrganizationId == organizationId && viewer.BotId == x.Id && viewer.UserId == userId)))
                .Select(x => new { x.Id, x.Name, x.WelcomeMessage, x.AccentColor, x.LogoVersion }).ToListAsync(ct));
        });
        group.MapPost("/", async (Guid organizationId, CreateBot request, IOrganizationAccess access, BotsDbContext db, CancellationToken ct) =>
        {
            if (!await access.HasAccessAsync(organizationId, true, ct)) return Results.NotFound();
            if (!IsValid(request))
                return Results.BadRequest();
            var bot = new Bot { OrganizationId = organizationId, Name = request.Name.Trim(), WelcomeMessage = request.WelcomeMessage ?? "", AccentColor = request.AccentColor ?? "#7441ec" };
            db.Bots.Add(bot);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/organizations/{organizationId}/bots/{bot.Id}", new { bot.Id, bot.Name, bot.WelcomeMessage, bot.AccentColor, bot.LogoVersion });
        });
        group.MapPatch("/{botId:guid}", async (Guid organizationId, Guid botId, CreateBot request,
            IOrganizationAccess access, BotsDbContext db, CancellationToken ct) =>
        {
            if (!await access.HasAccessAsync(organizationId, true, ct)) return Results.NotFound();
            if (!IsValid(request))
                return Results.BadRequest();
            var bot = await db.Bots.SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == botId, ct);
            if (bot is null) return Results.NotFound();
            bot.Name = request.Name.Trim(); bot.WelcomeMessage = request.WelcomeMessage ?? "";
            if (request.AccentColor is not null) bot.AccentColor = request.AccentColor;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
        group.MapGet("/{botId:guid}/knowledge-bases", async (Guid organizationId, Guid botId, IOrganizationAccess access, BotsDbContext db, CancellationToken ct) =>
        {
            if (!await access.HasAccessAsync(organizationId, true, ct)
                || !await db.Bots.AnyAsync(x => x.Id == botId && x.OrganizationId == organizationId, ct)) return Results.NotFound();
            return Results.Ok(new { KnowledgeBaseIds = await db.KnowledgeBases.Where(x => x.OrganizationId == organizationId && x.BotId == botId)
                .Select(x => x.KnowledgeBaseId).ToArrayAsync(ct) });
        });
        group.MapPut("/{botId:guid}/knowledge-bases", async (Guid organizationId, Guid botId, SetKnowledgeBases request,
            IOrganizationAccess access, IKnowledgeCatalog knowledge, BotsDbContext db, CancellationToken ct) =>
        {
            if (!await access.HasAccessAsync(organizationId, true, ct)
                || !await db.Bots.AnyAsync(x => x.Id == botId && x.OrganizationId == organizationId, ct)) return Results.NotFound();
            if (request.KnowledgeBaseIds is null) return Results.BadRequest();
            var ids = request.KnowledgeBaseIds.Distinct().ToArray();
            if (!await knowledge.ContainsAsync(organizationId, ids, ct)) return Results.NotFound();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM bots.bots WHERE id = {botId} AND organization_id = {organizationId} FOR UPDATE", ct);
            await db.KnowledgeBases.Where(x => x.OrganizationId == organizationId && x.BotId == botId).ExecuteDeleteAsync(ct);
            db.KnowledgeBases.AddRange(ids.Select(id => new BotKnowledgeBase { BotId = botId, OrganizationId = organizationId, KnowledgeBaseId = id }));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.NoContent();
        });
        group.MapGet("/{botId:guid}/viewers", async (Guid organizationId, Guid botId, IOrganizationAccess access, BotsDbContext db, CancellationToken ct) =>
        {
            if (!await access.HasAccessAsync(organizationId, true, ct)
                || !await db.Bots.AnyAsync(x => x.Id == botId && x.OrganizationId == organizationId, ct)) return Results.NotFound();
            return Results.Ok(new { UserIds = await db.Viewers.Where(x => x.OrganizationId == organizationId && x.BotId == botId)
                .Select(x => x.UserId).ToArrayAsync(ct) });
        });
        group.MapPut("/{botId:guid}/viewers", async (Guid organizationId, Guid botId, SetViewers request,
            IOrganizationAccess access, BotsDbContext db, CancellationToken ct) =>
        {
            if (!await access.HasAccessAsync(organizationId, true, ct)
                || !await db.Bots.AnyAsync(x => x.Id == botId && x.OrganizationId == organizationId, ct)) return Results.NotFound();
            if (request.UserIds is null) return Results.BadRequest();
            var ids = request.UserIds.Distinct().ToArray();
            if (!await access.AreViewersAsync(organizationId, ids, ct)) return Results.NotFound();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM bots.bots WHERE id = {botId} AND organization_id = {organizationId} FOR UPDATE", ct);
            await db.Viewers.Where(x => x.OrganizationId == organizationId && x.BotId == botId).ExecuteDeleteAsync(ct);
            db.Viewers.AddRange(ids.Select(id => new BotViewer { OrganizationId = organizationId, BotId = botId, UserId = id }));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.NoContent();
        });
    }
    private static bool IsValid(CreateBot request) => !string.IsNullOrWhiteSpace(request.Name) && request.Name.Trim().Length <= 200
        && (request.WelcomeMessage?.Length ?? 0) <= 2000
        && (request.AccentColor is null || new[] { "#7441ec", "#2457c5", "#186a47", "#a42d54" }.Contains(request.AccentColor));
    public sealed record CreateBot(string Name, string? WelcomeMessage, string? AccentColor = null);
    public sealed record SetKnowledgeBases(Guid[] KnowledgeBaseIds);
    public sealed record SetViewers(Guid[] UserIds);
}
