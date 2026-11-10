using DocParse.Modules.Access.Contracts;
using DocParse.Modules.Usage.Domain;
using DocParse.Modules.Usage.Contracts;
using DocParse.Modules.Usage.Infrastructure;

namespace DocParse.Modules.Usage;

public static class UsageModule
{
    public static IServiceCollection AddUsageModule(this IServiceCollection services,IConfiguration config) {
        services.AddScoped<UsageLedger>();services.AddScoped<UsageMeter>();services.AddScoped<IUsageMeter>(p=>p.GetRequiredService<UsageMeter>());services.AddScoped<IUsageMeasurements>(p=>p.GetRequiredService<UsageMeter>());
        services.AddHttpClient<FyrUsageClient>(http=>{http.BaseAddress=new Uri(config["FyrUsage:Endpoint"]??"http://127.0.0.1:3010/");http.Timeout=TimeSpan.FromSeconds(10);});services.AddHostedService<UsageOutboxWorker>();return services;
    }
    public static void MapUsageModule(this WebApplication app)
    {
        var group=app.MapGroup("/api/organizations/{organizationId:guid}/usage").RequireAuthorization();
        group.AddEndpointFilter(async(context,next)=>{var org=Guid.Parse(context.HttpContext.Request.RouteValues["organizationId"]!.ToString()!);var access=context.HttpContext.RequestServices.GetRequiredService<IOrganizationAccess>();return await access.HasAccessAsync(org,true,context.HttpContext.RequestAborted)?await next(context):Results.NotFound();});
        group.MapGet("/",async(Guid organizationId,UsageMeter meter,UsageLedger ledger,IConfiguration config,CancellationToken ct)=>config.GetValue<bool>("FyrUsage:Enabled")?Results.Ok(new{state=await meter.State(organizationId,ct),local=await ledger.LocalState(organizationId,ct),nextPlan=await ledger.Plan(organizationId,ct)}):Results.Problem(statusCode:503));
        group.MapGet("/plans",async(FyrUsageClient client,CancellationToken ct)=>Results.Ok(await client.SendAsync(HttpMethod.Get,"v2/credits/plans",null,ct)));
        group.MapGet("/breakdown",async(Guid organizationId,FyrUsageClient client,CancellationToken ct)=>Results.Ok(await client.SendAsync(HttpMethod.Get,"v2/credits/breakdown?subject="+organizationId,null,ct)));
        group.MapPut("/plan",async(Guid organizationId,SetPlan input,UsageLedger ledger,FyrUsageClient client,CancellationToken ct)=>{var plans=await client.SendAsync(HttpMethod.Get,"v2/credits/plans",null,ct);if(!plans.EnumerateArray().Any(p=>p.GetProperty("key").GetString()==input.PlanKey))return Results.BadRequest();await ledger.SetPlan(organizationId,input.PlanKey,ct);return Results.NoContent();});
        app.MapPost("/api/organizations/{organizationId:guid}/usage/estimate", async (Guid organizationId, CostModel model, IOrganizationAccess access, CancellationToken ct) =>
        {
            if (!await access.HasAccessAsync(organizationId, true, ct)) return Results.NotFound();
            try { return Results.Ok(CostCalculator.Calculate(model)); }
            catch (ArgumentException) { return Results.BadRequest(new { code = "validation" }); }
        }).RequireAuthorization();
    }
    private sealed record SetPlan(string PlanKey);
}
