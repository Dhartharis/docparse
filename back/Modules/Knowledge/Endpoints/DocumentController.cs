using Microsoft.AspNetCore.Mvc;
using DocParse.Modules.Knowledge.Application;
using DocParse.Modules.Knowledge.Contracts;
using DocParse.Modules.Knowledge.Infrastructure;

namespace DocParse.Modules.Knowledge.Endpoints;

[ApiController]
[Microsoft.AspNetCore.Authorization.Authorize]
[ServiceFilter(typeof(KnowledgeAdminFilter))]
[Route("api/organizations/{organizationId:guid}/knowledge-bases/{knowledgeBaseId:guid}/documents")]
public class DocumentController : ControllerBase
{
    private readonly DocumentExtractionService _extractionService;
    private readonly IDocumentStore _repository;
    private readonly IDocumentExporter _exportService;
    private readonly IOriginalFileStorage _fileStorage;

    public DocumentController(
        DocumentExtractionService extractionService,
        IDocumentStore repository,
        IDocumentExporter exportService,
        IOriginalFileStorage fileStorage)
    {
        _extractionService = extractionService;
        _repository = repository;
        _exportService = exportService;
        _fileStorage = fileStorage;
    }

    // POST /api/document/extract
    [HttpPost("extract")]
    [RequestSizeLimit(11 * 1024 * 1024)]
    public async Task<IActionResult> Extract(Guid organizationId, Guid knowledgeBaseId, IFormFile file, CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0)
            return BadRequest("No file provided.");
        if (file.Length > 10 * 1024 * 1024 || Path.GetExtension(file.FileName).ToLowerInvariant() is not (".pdf" or ".png" or ".jpg" or ".jpeg"))
            return ValidationProblem("Upload a PDF, PNG or JPEG file of at most 10 MiB.");

        await using var stream = file.OpenReadStream();
        var result = await _extractionService.ExtractAsync(stream, file.FileName, organizationId, knowledgeBaseId, cancellationToken);
        return AcceptedAtAction(nameof(GetById), new { organizationId, knowledgeBaseId, id = result.Id }, new { result.Id, result.ProcessingStatus,result.Deduplicated });
    }

    [HttpPost("{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid organizationId, Guid knowledgeBaseId, Guid id, CancellationToken cancellationToken)
    {
        if (await _repository.GetByIdAsync(id.ToString(), organizationId, knowledgeBaseId) is null) return NotFound();
        if (!await _repository.RetryAsync(id, organizationId, knowledgeBaseId, cancellationToken)) return Conflict();
        return Accepted(new { id, processingStatus = "queued" });
    }

    // GET /api/document/{id}
    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(Guid organizationId, Guid knowledgeBaseId, string id)
    {
        var document = await _repository.GetByIdAsync(id, organizationId, knowledgeBaseId);
        if (document == null)
            return NotFound();

        return Ok(new DocumentDetails(DocumentSummary.From(document), document.Fields));
    }

    [HttpGet("{id}/file")]
    public async Task<IActionResult> GetOriginalFile(Guid organizationId, Guid knowledgeBaseId, string id, CancellationToken cancellationToken)
    {
        var document = await _repository.GetByIdAsync(id, organizationId, knowledgeBaseId);
        if (document == null || string.IsNullOrWhiteSpace(document.OriginalBlobName))
            return NotFound();

        var download = await _fileStorage.DownloadAsync(document.OriginalBlobName, cancellationToken);
        return File(
            download,
            document.OriginalContentType ?? "application/octet-stream",
            document.OriginalFileName ?? "document");
    }

    // GET /api/document/all
    [HttpGet("all")]
    public async Task<IActionResult> GetAll(Guid organizationId, Guid knowledgeBaseId)
    {
        var documents = await _repository.GetAllAsync(organizationId, knowledgeBaseId);
        return Ok(documents.Select(DocumentSummary.From));
    }

    // GET /api/document/{id}/export/csv
    [HttpGet("{id}/export/csv")]
    public async Task<IActionResult> ExportCsv(Guid organizationId, Guid knowledgeBaseId, string id)
    {
        var document = await _repository.GetByIdAsync(id, organizationId, knowledgeBaseId);
        if (document == null)
            return NotFound();

        if (document.ProcessingStatus != "ready") return Conflict();
        var csvBytes = _exportService.ToCsv(document);
        return File(csvBytes, "text/csv", $"document_{id}.csv");
    }

    // GET /api/document/{id}/export/excel
    [HttpGet("{id}/export/excel")]
    public async Task<IActionResult> ExportExcel(Guid organizationId, Guid knowledgeBaseId, string id)
    {
        var document = await _repository.GetByIdAsync(id, organizationId, knowledgeBaseId);
        if (document == null)
            return NotFound();

        if (document.ProcessingStatus != "ready") return Conflict();
        var excelBytes = _exportService.ToExcel(document);
        return File(excelBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"document_{id}.xlsx");
    }
}
