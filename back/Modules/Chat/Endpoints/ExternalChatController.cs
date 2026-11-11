using DocParse.Modules.Bots.Contracts;
using DocParse.Modules.Chat.Application;
using Microsoft.AspNetCore.Mvc;

namespace DocParse.Modules.Chat.Endpoints;

[ApiController]
public sealed class ExternalChatController(IBotChannelAccess channels, QueryService chat):ControllerBase
{
    [HttpPost("api/v1/organizations/{organizationId:guid}/bots/{botId:guid}/messages")]
    [RequestSizeLimit(65536)]
    public async Task<IActionResult> Api(Guid organizationId,Guid botId,ChatController.ChatRequest request,CancellationToken ct)
    {
        if(!Valid(request))return BadRequest(new{code="validation"});
        var bot=await channels.AuthenticateKeyAsync(organizationId,botId,Request.Headers["X-DocParse-Key"].ToString(),ct);
        if(bot is null)return Unauthorized();
        return Ok(await chat.QueryDocumentsAsync(bot.Bot.OrganizationId,bot.Bot.KnowledgeBaseIds,request.Question.Trim(),request.History??[],ct,botId,"api"));
    }
    [HttpPost("api/embed/{botId:guid}/messages")]
    [RequestSizeLimit(65536)]
    public async Task<IActionResult> Embed(Guid botId,ChatController.ChatRequest request,CancellationToken ct)
    {
        if(!Valid(request))return BadRequest(new{code="validation"});
        var bot=await channels.AuthenticateEmbedAsync(botId,Request.Headers["X-DocParse-Session"].ToString(),ct);
        if(bot is null)return Unauthorized();
        return Ok(await chat.QueryDocumentsAsync(bot.Bot.OrganizationId,bot.Bot.KnowledgeBaseIds,request.Question.Trim(),request.History??[],ct,botId,"embed"));
    }
    private static bool Valid(ChatController.ChatRequest request)=>!string.IsNullOrWhiteSpace(request.Question)&&request.Question.Length<=8000
        && (request.History is null || request.History.Count<=4&&request.History.All(t=>t is not null&&!string.IsNullOrWhiteSpace(t.Question)&&t.Question.Length<=400&&!string.IsNullOrWhiteSpace(t.Answer)&&t.Answer.Length<=800));
}
