using Microsoft.AspNetCore.Mvc;
using DocParse.Application.Services;
using DocParse.Application.DTOs;
using DocParse.Infrastructure.MongoDB;

namespace DocParse.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DocumentController : ControllerBase
{
    private readonly DocumentExtractionService _extractionService;
    private readonly DocumentRepository _repository;
    private readonly ExportService _exportService;

    public DocumentController(
        DocumentExtractionService extractionService,
        DocumentRepository repository,
        ExportService exportService)
    {
        _extractionService = extractionService;
        _repository = repository;
        _exportService = exportService;
    }

    // POST /api/document/extract
    [HttpPost("extract")]
    public async Task<IActionResult> Extract(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest("No file provided.");

        var result = await _extractionService.ExtractAsync(file);
        return Ok(result);
    }

    // GET /api/document/{id}
    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        var document = await _repository.GetByIdAsync(id);
        if (document == null)
            return NotFound();

        return Ok(document);
    }

    // GET /api/document/all
    [HttpGet("all")]
    public async Task<IActionResult> GetAll()
    {
        var documents = await _repository.GetAllAsync();
        return Ok(documents);
    }

    // GET /api/document/{id}/export/csv
    [HttpGet("{id}/export/csv")]
    public async Task<IActionResult> ExportCsv(string id)
    {
        var document = await _repository.GetByIdAsync(id);
        if (document == null)
            return NotFound();

        var csvBytes = _exportService.ToCsv(document);
        return File(csvBytes, "text/csv", $"document_{id}.csv");
    }

    // GET /api/document/{id}/export/excel
    [HttpGet("{id}/export/excel")]
    public async Task<IActionResult> ExportExcel(string id)
    {
        var document = await _repository.GetByIdAsync(id);
        if (document == null)
            return NotFound();

        var excelBytes = _exportService.ToExcel(document);
        return File(excelBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"document_{id}.xlsx");
    }
}