using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmmGab.Application.Abstractions;
using SmmGab.Data;

namespace SmmGab.Controllers.Api;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class FilesController : ControllerBase
{
    private readonly IFileStorageService _fileStorageService;
    private readonly ApplicationDbContext _context;

    public FilesController(IFileStorageService fileStorageService, ApplicationDbContext context)
    {
        _fileStorageService = fileStorageService;
        _context = context;
    }

    [HttpPost("upload")]
    public async Task<ActionResult<Domain.Models.FileStorage>> UploadFile(IFormFile file, CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0)
            return BadRequest("File is required");

        var maxSize = 2147483648L; // 2GB
        if (file.Length > maxSize)
            return BadRequest("File size exceeds maximum allowed size");

        var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".mp4", ".avi", ".mov", ".pdf" };
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!allowedExtensions.Contains(extension))
            return BadRequest("File type not allowed");

        await using var stream = file.OpenReadStream();
        var fileStorage = await _fileStorageService.SaveFileAsync(stream, file.FileName, file.ContentType, file.Length, cancellationToken);

        return Ok(fileStorage);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Domain.Models.FileStorage>> GetFile(Guid id)
    {
        var file = await _fileStorageService.GetFileAsync(id, CancellationToken.None);
        if (file == null)
            return NotFound();

        return Ok(file);
    }

    [HttpGet("{id}/download")]
    public async Task<IActionResult> DownloadFile(Guid id)
    {
        var file = await _fileStorageService.GetFileAsync(id, CancellationToken.None);
        if (file == null)
            return NotFound();

        var stream = await _fileStorageService.GetFileStreamAsync(id, CancellationToken.None);
        if (stream == null)
            return NotFound();

        return File(stream, file.ContentType, file.StoredFileName);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteFile(Guid id)
    {
        var result = await _fileStorageService.DeleteFileAsync(id, CancellationToken.None);
        if (!result)
            return NotFound();

        return NoContent();
    }
}

