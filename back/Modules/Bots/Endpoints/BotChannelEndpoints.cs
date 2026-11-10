using DocParse.Modules.Access.Contracts;
using DocParse.Modules.Bots.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DocParse.Modules.Bots.Endpoints;

internal static class BotChannelEndpoints
{
    public static void MapBotChannels(this WebApplication app)
    {
        var group=app.MapGroup("/api/organizations/{organizationId:guid}/bots/{botId:guid}/channels").RequireAuthorization();
        group.AddEndpointFilter(async(context,next)=> {
            var org=Guid.Parse(context.HttpContext.Request.RouteValues["organizationId"]!.ToString()!);
            var bot=Guid.Parse(context.HttpContext.Request.RouteValues["botId"]!.ToString()!);
            var access=context.HttpContext.RequestServices.GetRequiredService<IOrganizationAccess>();
            var db=context.HttpContext.RequestServices.GetRequiredService<BotsDbContext>();
            return !await access.HasAccessAsync(org,true,context.HttpContext.RequestAborted)||!await db.Bots.AnyAsync(x=>x.Id==bot&&x.OrganizationId==org,context.HttpContext.RequestAborted)?Results.NotFound():await next(context);
        });
        group.MapGet("/keys",async(Guid organizationId,Guid botId,BotChannelStore store,CancellationToken ct)=>Results.Ok(await store.ListAsync(organizationId,botId,ct)));
        group.MapPost("/keys",async(Guid organizationId,Guid botId,CreateKey request,BotChannelStore store,CancellationToken ct)=> {
            try {var issued=await store.IssueAsync(organizationId,botId,request.Name,request.ExpiresInDays,request.RotateKeyId,ct);return issued is null?Results.NotFound():Results.Ok(issued);}
            catch(ArgumentException){return Results.BadRequest(new{code="validation"});}
        });
        group.MapDelete("/keys/{keyId:guid}",async(Guid organizationId,Guid botId,Guid keyId,BotChannelStore store,CancellationToken ct)=>{await store.RevokeAsync(organizationId,botId,keyId,ct);return Results.NoContent();});
        group.MapGet("/embed",async(Guid organizationId,Guid botId,BotChannelStore store,CancellationToken ct)=>Results.Ok(await store.SettingsAsync(organizationId,botId,ct)));
        group.MapPut("/embed",async(Guid organizationId,Guid botId,EmbedSettings request,BotChannelStore store,CancellationToken ct)=> {
            try{await store.ConfigureAsync(organizationId,botId,request,ct);return Results.NoContent();}catch(ArgumentException){return Results.BadRequest(new{code="validation"});}
        });
    }
    private sealed record CreateKey(string Name,int ExpiresInDays=90,Guid? RotateKeyId=null);
}
