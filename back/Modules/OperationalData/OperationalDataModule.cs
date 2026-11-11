using DocParse.Modules.Access.Contracts;
using DocParse.Modules.Integrations.Contracts;
using DocParse.Modules.OperationalData.Contracts;
using DocParse.Modules.OperationalData.Infrastructure;

namespace DocParse.Modules.OperationalData;

public static class OperationalDataModule
{
    public static IServiceCollection AddOperationalDataModule(this IServiceCollection services)
    {
        services.AddScoped<IOperationalProjection, OperationalProjection>();
        return services;
    }
    public static void MapOperationalDataModule(this WebApplication app)
    {
        app.MapGet("/api/organizations/{organizationId:guid}/connections/{connectionId:guid}/records",
            async (Guid organizationId, Guid connectionId, IOrganizationAccess access, IConnectionCatalog connections, IOperationalProjection projection,
                HttpContext context, TimeProvider clock, CancellationToken ct) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (!await access.HasAccessAsync(organizationId, true, ct)) return Results.NotFound();
                var source = await connections.GetActiveAsync(organizationId, connectionId, ct);
                return source is null ? Results.NotFound() : Results.Ok(await projection.ReadAsync(organizationId, connectionId,
                    source.PriceMaxAgeSeconds, source.StockMaxAgeSeconds, clock.GetUtcNow(), ct));
            }).RequireAuthorization();
    }
}
