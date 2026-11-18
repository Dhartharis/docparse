using DocParse.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace DocParse.Controllers;

[ApiController]
[Route("api/[controller]")]
public class QueryController : ControllerBase
{
    private readonly QueryService _queryService;
    private readonly IConfiguration _configuration;

    public QueryController(QueryService queryService, IConfiguration configuration)
    {
        _queryService = queryService;
        _configuration = configuration;
    }

    [HttpPost]
    public async Task<IActionResult> Query([FromBody] QueryRequest request)
    {
        var apiKey = _configuration["OpenAI:ApiKey"]!;
        var answer = await _queryService.QueryDocumentsAsync(request.Question, apiKey);
        return Ok(new { answer });
    }
}

public record QueryRequest(string Question);