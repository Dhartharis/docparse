using DocParse.Modules.Access.Contracts;
using DocParse.Modules.Integrations.Application;
using DocParse.Modules.Integrations.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text.Json.Serialization;

namespace DocParse.Modules.Integrations;

public static class IntegrationsModule
{
    public static IServiceCollection AddIntegrationsModule(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in configuration.GetSection("Integrations:AllowedProviderOrigins").Get<string[]>() ?? [])
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.PathAndQuery != "/" || uri.Fragment.Length != 0 ||
                (uri.Scheme != "https" && !(environment.IsDevelopment() && uri.Scheme == "http" && (uri.IsLoopback || uri.Host == "host.docker.internal"))))
                throw new InvalidOperationException("Provider origins must be trusted HTTPS origins or explicit Development loopback origins.");
            origins.Add(uri.GetLeftPart(UriPartial.Authority));
        }
        services.AddSingleton(new ProviderPolicy(environment.IsDevelopment(), origins));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IConnectionStore, ConnectionStore>();
        return services;
    }

    public static void MapIntegrationsModule(this WebApplication app)
    {
        var group = app.MapGroup("/api/organizations/{organizationId:guid}/connections").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            var org = Guid.Parse(context.HttpContext.Request.RouteValues["organizationId"]!.ToString()!);
            var access = context.HttpContext.RequestServices.GetRequiredService<IOrganizationAccess>();
            if (!await access.HasAccessAsync(org, true, context.HttpContext.RequestAborted)) return Results.NotFound();
            try { return await next(context); }
            catch (ConnectionFailure error)
            {
                var status = error.Kind == ConnectionFailureKind.Conflict ? 409 : 400;
                var code = error.Kind switch { ConnectionFailureKind.OriginNotAllowed => "connection_origin", ConnectionFailureKind.Conflict => "connection_conflict", _ => "validation" };
                return Results.Problem(statusCode: status, title: "The connection operation could not be completed.", extensions: new Dictionary<string, object?> { ["code"] = code });
            }
        });
        group.MapGet("/", async (Guid organizationId, IConnectionStore store, ProviderPolicy policy, CancellationToken ct) =>
            Results.Ok(new { connections = await store.ListAsync(organizationId, ct), providerOriginsConfigured = policy.AllowedOrigins.Count > 0 }));
        group.MapPost("/", async (Guid organizationId, CreateRequest request, IConnectionStore store, ProviderPolicy policy, CancellationToken ct) =>
        {
            if (request.RequestId == Guid.Empty || request.Definition is null) throw new ConnectionFailure(ConnectionFailureKind.Invalid);
            return Results.Ok(await store.CreateAsync(organizationId, request.RequestId, request.Definition.ToDefinition().Validate(policy), ct));
        }).RequireRateLimiting("authentication");
        group.MapPut("/{connectionId:guid}", async (Guid organizationId, Guid connectionId, UpdateRequest request, IConnectionStore store, CancellationToken ct) =>
        {
            if (request.Revision == Guid.Empty || request.Settings is null) throw new ConnectionFailure(ConnectionFailureKind.Invalid);
            var result = await store.UpdateAsync(organizationId, connectionId, request.Revision, request.Settings.ToSettings(), ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequireRateLimiting("authentication");
        group.MapPost("/{connectionId:guid}/push-key", async (Guid organizationId, Guid connectionId, RotateRequest request, IConnectionStore store, CancellationToken ct) =>
        {
            if (request.Revision == Guid.Empty) throw new ConnectionFailure(ConnectionFailureKind.Invalid);
            var result = await store.RotatePushAsync(organizationId, connectionId, request.Revision, request.Days, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequireRateLimiting("authentication");
        group.MapPut("/{connectionId:guid}/pull-key", async (Guid organizationId, Guid connectionId, PullKeyRequest request, IConnectionStore store, CancellationToken ct) =>
        {
            if (request.Revision == Guid.Empty) throw new ConnectionFailure(ConnectionFailureKind.Invalid);
            var result = await store.SetPullKeyAsync(organizationId, connectionId, request.Revision, request.KeyId ?? "", request.SecretHex ?? "", ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequireRateLimiting("authentication");
        group.MapPost("/{connectionId:guid}/disconnect", async (Guid organizationId, Guid connectionId, RevisionRequest request, IConnectionStore store, CancellationToken ct) =>
        {
            if (request.Revision == Guid.Empty) throw new ConnectionFailure(ConnectionFailureKind.Invalid);
            return await store.DisconnectAsync(organizationId, connectionId, request.Revision, ct) ? Results.NoContent() : Results.NotFound();
        }).RequireRateLimiting("authentication");
    }

    private sealed record SettingsRequest(
        [property: JsonRequired] string? Name,
        [property: JsonRequired] int PriceMaxAgeSeconds,
        [property: JsonRequired] int StockMaxAgeSeconds,
        [property: JsonRequired] int ClockSkewSeconds,
        [property: JsonRequired] int? SyncIntervalSeconds)
    {
        public ConnectionSettings ToSettings() => new(Name ?? "", PriceMaxAgeSeconds, StockMaxAgeSeconds, ClockSkewSeconds, SyncIntervalSeconds);
    }
    private sealed record DefinitionRequest(
        [property: JsonRequired] string? SourceBusinessId,
        [property: JsonRequired] string? BaseUrl,
        [property: JsonRequired] bool Push,
        [property: JsonRequired] bool PullLive,
        [property: JsonRequired] bool PullScheduled,
        [property: JsonRequired] int PushKeyDays,
        [property: JsonRequired] SettingsRequest? Settings)
    {
        public ConnectionDefinition ToDefinition() => new(SourceBusinessId ?? "", BaseUrl, Push, PullLive, PullScheduled, PushKeyDays,
            Settings?.ToSettings() ?? throw new ConnectionFailure(ConnectionFailureKind.Invalid));
    }
    private sealed record CreateRequest(Guid RequestId, DefinitionRequest? Definition);
    private sealed record UpdateRequest(Guid Revision, SettingsRequest? Settings);
    private sealed record RotateRequest(Guid Revision, int Days);
    private sealed record PullKeyRequest(Guid Revision, string? KeyId, string? SecretHex);
    private sealed record RevisionRequest(Guid Revision);
}
