using DocParse.Modules.Access.Contracts;

using DocParse.Modules.Knowledge.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace DocParse.Modules.Knowledge.Endpoints;

public sealed class KnowledgeAdminFilter(IOrganizationAccess access, KnowledgeDbContext database) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var organizationId = Guid.Parse(context.RouteData.Values["organizationId"]!.ToString()!);
        var knowledgeBaseId = Guid.Parse(context.RouteData.Values["knowledgeBaseId"]!.ToString()!);
        if (!await access.HasAccessAsync(organizationId, true, context.HttpContext.RequestAborted)
            || !await database.KnowledgeBases.AnyAsync(x => x.Id == knowledgeBaseId && x.OrganizationId == organizationId, context.HttpContext.RequestAborted))
        {
            context.Result = new NotFoundResult();
            return;
        }
        await next();
    }
}
