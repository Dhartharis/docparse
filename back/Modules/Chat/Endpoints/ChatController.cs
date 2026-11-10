using DocParse.Modules.Access.Contracts;

using DocParse.Modules.Bots.Contracts;
using DocParse.Modules.Chat.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DocParse.Modules.Chat.Endpoints;

[ApiController]
[Authorize]
[Route("api/organizations/{organizationId:guid}/bots/{botId:guid}/messages")]
public sealed class ChatController(QueryService chat, IBotCatalog bots) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Send(Guid organizationId, Guid botId, ChatRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > 8000)
            return ValidationProblem("A question of 1–8000 characters is required.");
        var history = request.History ?? [];
        if (history.Count > 4 || history.Any(turn => turn is null || string.IsNullOrWhiteSpace(turn.Question) || string.IsNullOrWhiteSpace(turn.Answer)
            || turn.Question.Length > 400 || turn.Answer.Length > 800)) return ValidationProblem("History must contain at most four short question/answer pairs.");
        var bot = await bots.GetForCurrentUserAsync(organizationId, botId, cancellationToken);
        if (bot is null) return NotFound();
        return Ok(await chat.QueryDocumentsAsync(organizationId, bot.KnowledgeBaseIds, request.Question.Trim(), history, cancellationToken,botId,"web"));
    }
    public sealed record ChatRequest(string Question, IReadOnlyList<ChatTurn>? History = null);
}
