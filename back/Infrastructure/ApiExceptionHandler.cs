using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Microsoft.EntityFrameworkCore;

namespace DocParse.Infrastructure;

internal sealed class ApiExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var status = exception switch
        {
            BadHttpRequestException request => request.StatusCode,
            DbUpdateException{InnerException:PostgresException{SqlState:"23505",ConstraintName:"UserNameIndex"}} => StatusCodes.Status409Conflict,
            DbUpdateException{InnerException:PostgresException{SqlState:"23505",ConstraintName:"UniqueNormalizedEmail"}} => StatusCodes.Status409Conflict,
            DbUpdateException{InnerException:PostgresException{SqlState:"23505",ConstraintName:"organization_creation_request"}} => StatusCodes.Status409Conflict,
            DocParse.Modules.Access.InvitationUnavailable => StatusCodes.Status404NotFound,
            DocParse.Modules.Access.InvitationInvalid => StatusCodes.Status400BadRequest,
            DocParse.Modules.Access.InvitationMemberExists => StatusCodes.Status409Conflict,
            DocParse.Modules.Access.InvitationCooldown => StatusCodes.Status429TooManyRequests,
            DbUpdateException {InnerException:PostgresException{SqlState:"23514",ConstraintName:"active_knowledge_base" or "knowledge_base_linked"}} => StatusCodes.Status409Conflict,
            DbUpdateException {InnerException:PostgresException{SqlState:"23505",ConstraintName:"document_active_content"}} => StatusCodes.Status409Conflict,
            PostgresException {SqlState:"23514",ConstraintName:"knowledge_base_linked" or "active_knowledge_base"} => StatusCodes.Status409Conflict,
            PostgresException {SqlState:"23505",ConstraintName:"document_active_content"} => StatusCodes.Status409Conflict,
            DocParse.Modules.Usage.Contracts.UsageLimitReached => StatusCodes.Status429TooManyRequests,
            DocParse.Modules.Usage.Contracts.UsageServiceUnavailable => StatusCodes.Status503ServiceUnavailable,
            DocParse.Modules.Bots.Infrastructure.ChannelRateExceeded => StatusCodes.Status429TooManyRequests,
            DocParse.Modules.Knowledge.Application.DocumentUploadRejected => StatusCodes.Status400BadRequest,
            DocParse.Modules.Chat.Application.AnswerGenerationUnavailable => StatusCodes.Status503ServiceUnavailable,
            DocParse.Modules.Knowledge.Application.DocumentProcessingUnavailable => StatusCodes.Status503ServiceUnavailable,
            OperationCanceledException when !context.RequestAborted.IsCancellationRequested => StatusCodes.Status504GatewayTimeout,
            HttpRequestException or Azure.RequestFailedException or InvalidDataException => StatusCodes.Status502BadGateway,
            DbUpdateException { InnerException: PostgresException { SqlState: "23505", ConstraintName: "IX_invitations_OrganizationId_Email" } } => StatusCodes.Status409Conflict,
            DbUpdateException { InnerException: PostgresException { SqlState: "23503", ConstraintName: "bot_viewer_membership" } } => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError
        };
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status,
            Extensions = exception is DbUpdateException{InnerException:PostgresException{ConstraintName:"UniqueNormalizedEmail"}} ? new Dictionary<string,object?>{["code"]="account_exists"} : exception is PostgresException{ConstraintName:"knowledge_base_linked"} ? new Dictionary<string,object?>{["code"]="knowledge_base_linked"} : exception is PostgresException{ConstraintName:"document_active_content"} ? new Dictionary<string,object?>{["code"]="document_duplicate"} : exception is DocParse.Modules.Usage.Contracts.UsageLimitReached ? new Dictionary<string,object?>{["code"]="usage_limit"} : exception is DocParse.Modules.Usage.Contracts.UsageServiceUnavailable ? new Dictionary<string,object?>{["code"]="usage_unavailable"} : new Dictionary<string,object?>(),
            Title = status switch { 400 => "Invalid request.", 409 => "Resource already exists.", 502 => "An upstream service is unavailable.", 503 => "Document processing is unavailable.", _ => "The request could not be completed." }
        }, cancellationToken);
        return true;
    }
}
