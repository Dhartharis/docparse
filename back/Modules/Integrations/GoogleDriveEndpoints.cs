using DocParse.Modules.Access.Contracts;
using DocParse.Modules.Integrations.Application;
using DocParse.Modules.Integrations.Infrastructure;

namespace DocParse.Modules.Integrations;

internal static class GoogleDriveEndpoints
{
    public static void MapGoogleDriveEndpoints(this WebApplication app)
    {
        var group=app.MapGroup("/api/organizations/{organizationId:guid}/google-drive").RequireAuthorization().RequireRateLimiting("authentication");
        group.AddEndpointFilter(async(context,next)=>
        {
            context.HttpContext.Response.Headers.CacheControl="no-store";
            var org=Guid.Parse(context.HttpContext.Request.RouteValues["organizationId"]!.ToString()!);
            var access=context.HttpContext.RequestServices.GetRequiredService<IOrganizationAccess>();
            if(!await access.HasAccessAsync(org,true,context.HttpContext.RequestAborted))return Results.NotFound();
            var options=context.HttpContext.RequestServices.GetRequiredService<GoogleDriveOptions>();
            if(!options.Enabled&&context.HttpContext.Request.Method!="GET"&&!context.HttpContext.Request.Path.Value!.EndsWith("/disconnect",StringComparison.OrdinalIgnoreCase))return Failure("google_disabled",409);
            try
            {
                var result=await next(context);
                return await access.HasAccessAsync(org,true,context.HttpContext.RequestAborted)?result:Results.NotFound();
            }
            catch(GoogleDriveFailure error){return Failure(error.Code,error.Code switch{"google_busy"=>409,"google_unavailable"=>503,"google_response"=>502,_=>400});}
            catch(CatalogFailure error){return Failure(error.Code,400);}
        });
        group.MapGet("/",async(Guid organizationId,IGoogleDriveAccounts accounts,IOrganizationAccess access,GoogleDriveOptions options,CancellationToken ct)=>
            Results.Ok(new{enabled=options.Enabled,accounts=await accounts.ListAsync(organizationId,access.UserId,ct)}));
        group.MapPost("/authorize",async(Guid organizationId,GoogleDriveSessions sessions,IOrganizationAccess access,CancellationToken ct)=>
            Results.Ok(await sessions.StartAsync(organizationId,access.UserId,ct)));
        group.MapPost("/complete",async(Guid organizationId,CompleteRequest request,GoogleDriveSessions sessions,IOrganizationAccess access,CancellationToken ct)=>
            Results.Ok(new{id=await sessions.CompleteAsync(organizationId,access.UserId,request.State??"",request.Code??"",ct)}));
        group.MapPost("/{accountId:guid}/picker",async(Guid organizationId,Guid accountId,GoogleDriveSessions sessions,IOrganizationAccess access,GoogleDriveOptions options,CancellationToken ct)=>
            Results.Ok(new{accessToken=await sessions.PickerTokenAsync(organizationId,access.UserId,accountId,ct),apiKey=options.PickerApiKey,projectNumber=options.ProjectNumber}));
        group.MapPost("/{accountId:guid}/inspect",async(Guid organizationId,Guid accountId,PreviewRequest request,GoogleDriveSessions sessions,IOrganizationAccess access,CancellationToken ct)=>
        {var result=await sessions.PreviewAsync(organizationId,access.UserId,accountId,request.FileId??"",request.Sheet,request.HeaderRow,null,ct);return Results.Ok(new{file=result.File,inspection=result.Inspection});});
        group.MapPost("/{accountId:guid}/preview",async(Guid organizationId,Guid accountId,PreviewRequest request,GoogleDriveSessions sessions,IOrganizationAccess access,CancellationToken ct)=>
        {
            if(request.Mapping is null)throw new CatalogFailure("catalog_mapping");
            var result=await sessions.PreviewAsync(organizationId,access.UserId,accountId,request.FileId??"",request.Sheet,request.Mapping.HeaderRow,request.Mapping,ct);return Results.Ok(new{file=result.File,preview=result.Preview});
        });
        group.MapPost("/{accountId:guid}/disconnect",async(Guid organizationId,Guid accountId,IGoogleDriveAccounts accounts,IOrganizationAccess access,CancellationToken ct)=>
        {await accounts.DisconnectAsync(organizationId,access.UserId,accountId,ct);return Results.NoContent();});
    }
    private static IResult Failure(string code,int status)=>Results.Problem(statusCode:status,title:"The Google Drive operation could not be completed.",extensions:new Dictionary<string,object?>{["code"]=code});
    private sealed record CompleteRequest(string? State,string? Code);
    private sealed record PreviewRequest(string? FileId,string? Sheet,int HeaderRow,CatalogMapping? Mapping);
}
