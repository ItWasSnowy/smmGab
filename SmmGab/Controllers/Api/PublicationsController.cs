using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmmGab.Application.Abstractions;
using SmmGab.Data;
using SmmGab.Domain.Enums;
using SmmGab.Domain.Models;

namespace SmmGab.Controllers.Api;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PublicationsController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IPublisherFactory _publisherFactory;

    public PublicationsController(ApplicationDbContext context, IPublisherFactory publisherFactory)
    {
        _context = context;
        _publisherFactory = publisherFactory;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Publication>>> GetPublications([FromQuery] Guid? projectId)
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        var query = _context.Publications
            .Include(p => p.Project)
            .Include(p => p.Targets)
            .Include(p => p.Files)
            .Where(p => p.Project.OwnerId == userId);

        if (projectId.HasValue)
            query = query.Where(p => p.ProjectId == projectId.Value);

        var publications = await query.ToListAsync();
        return Ok(publications);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Publication>> GetPublication(Guid id)
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        var publication = await _context.Publications
            .Include(p => p.Project)
            .Include(p => p.Targets)
            .ThenInclude(t => t.Channel)
            .Include(p => p.Files)
            .FirstOrDefaultAsync(p => p.Id == id && p.Project.OwnerId == userId);

        if (publication == null)
            return NotFound();

        return Ok(publication);
    }

    [HttpPost]
    public async Task<ActionResult<Publication>> CreatePublication([FromBody] CreatePublicationDto dto)
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        
        var project = await _context.Projects
            .FirstOrDefaultAsync(p => p.Id == dto.ProjectId && p.OwnerId == userId);

        if (project == null)
            return BadRequest("Project not found");

        var publication = new Publication
        {
            Id = Guid.NewGuid(),
            ProjectId = dto.ProjectId,
            Text = dto.Text,
            DeltaQuill = dto.DeltaQuill,
            AuthorId = userId,
            CreatedAtUtc = DateTime.UtcNow,
            IsPublish = dto.IsPublish,
            IsNow = dto.IsNow,
            IsLater = dto.IsLater,
            ScheduledAtUtc = dto.ScheduledAtUtc,
            ClientTimezoneMinutes = dto.ClientTimezoneMinutes,
            Status = dto.IsNow ? PublicationStatus.Scheduled : (dto.IsPublish ? PublicationStatus.Scheduled : PublicationStatus.Draft)
        };

        // Создаем цели публикации
        foreach (var targetDto in dto.Targets)
        {
            var channel = await _context.Channels
                .Include(c => c.Project)
                .FirstOrDefaultAsync(c => c.Id == targetDto.ChannelId && c.Project.OwnerId == userId);

            if (channel == null)
                continue;

            var target = new PublicationTarget
            {
                Id = Guid.NewGuid(),
                PublicationId = publication.Id,
                ChannelId = targetDto.ChannelId,
                ChannelType = channel.Type,
                CustomParamsJson = targetDto.CustomParamsJson,
                Status = publication.IsNow ? TargetStatus.Scheduled : TargetStatus.Scheduled,
                RetryCount = 0
            };

            publication.Targets.Add(target);
        }

        // Связываем файлы
        if (dto.FileIds != null && dto.FileIds.Any())
        {
            var files = await _context.FileStorage
                .Where(f => dto.FileIds.Contains(f.Id))
                .ToListAsync();

            foreach (var file in files)
            {
                file.PublicationId = publication.Id;
            }
        }

        _context.Publications.Add(publication);
        await _context.SaveChangesAsync();

        // Если немедленная публикация - запускаем публикацию
        if (publication.IsNow)
        {
            _ = Task.Run(async () => await PublishPublicationAsync(publication.Id));
        }

        return CreatedAtAction(nameof(GetPublication), new { id = publication.Id }, publication);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdatePublication(Guid id, [FromBody] UpdatePublicationDto dto)
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        var publication = await _context.Publications
            .Include(p => p.Project)
            .Include(p => p.Targets)
            .FirstOrDefaultAsync(p => p.Id == id && p.Project.OwnerId == userId);

        if (publication == null)
            return NotFound();

        publication.Text = dto.Text;
        publication.DeltaQuill = dto.DeltaQuill;
        publication.IsPublish = dto.IsPublish;
        publication.IsNow = dto.IsNow;
        publication.IsLater = dto.IsLater;
        publication.ScheduledAtUtc = dto.ScheduledAtUtc;
        publication.ClientTimezoneMinutes = dto.ClientTimezoneMinutes;

        if (dto.IsNow && publication.Status == PublicationStatus.Draft)
        {
            publication.Status = PublicationStatus.Scheduled;
            _ = Task.Run(async () => await PublishPublicationAsync(publication.Id));
        }

        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeletePublication(Guid id)
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        var publication = await _context.Publications
            .Include(p => p.Project)
            .FirstOrDefaultAsync(p => p.Id == id && p.Project.OwnerId == userId);

        if (publication == null)
            return NotFound();

        _context.Publications.Remove(publication);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id}/publish-now")]
    public async Task<IActionResult> PublishNow(Guid id)
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        var publication = await _context.Publications
            .Include(p => p.Project)
            .FirstOrDefaultAsync(p => p.Id == id && p.Project.OwnerId == userId);

        if (publication == null)
            return NotFound();

        publication.IsNow = true;
        publication.Status = PublicationStatus.Scheduled;
        await _context.SaveChangesAsync();

        _ = Task.Run(async () => await PublishPublicationAsync(publication.Id));

        return Ok();
    }

    [HttpGet("calendar")]
    public async Task<ActionResult<IEnumerable<Publication>>> GetCalendar([FromQuery] int year, [FromQuery] int month, [FromQuery] Guid? projectId)
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        var startDate = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var endDate = startDate.AddMonths(1);

        var query = _context.Publications
            .Include(p => p.Project)
            .Where(p => p.Project.OwnerId == userId && 
                       p.ScheduledAtUtc.HasValue &&
                       p.ScheduledAtUtc >= startDate &&
                       p.ScheduledAtUtc < endDate);

        if (projectId.HasValue)
            query = query.Where(p => p.ProjectId == projectId.Value);

        var publications = await query.ToListAsync();
        return Ok(publications);
    }

    [HttpGet("project/{projectId}")]
    public async Task<ActionResult<IEnumerable<Publication>>> GetProjectPublications(
        Guid projectId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] PublicationStatus? status = null)
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        var query = _context.Publications
            .Include(p => p.Project)
            .Where(p => p.ProjectId == projectId && p.Project.OwnerId == userId);

        if (status.HasValue)
            query = query.Where(p => p.Status == status.Value);

        var total = await query.CountAsync();
        var publications = await query
            .OrderByDescending(p => p.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return Ok(new { Items = publications, Total = total, Page = page, PageSize = pageSize });
    }

    private async Task PublishPublicationAsync(Guid publicationId)
    {
        try
        {
            var publication = await _context.Publications
                .Include(p => p.Targets)
                .ThenInclude(t => t.Channel)
                .FirstOrDefaultAsync(p => p.Id == publicationId);

            if (publication == null) return;

            publication.Status = PublicationStatus.Publishing;
            await _context.SaveChangesAsync();

            var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5)); // Таймаут 5 минут для публикации

            var tasks = publication.Targets.Select(async target =>
            {
                try
                {
                    target.Status = TargetStatus.Publishing;
                    await _context.SaveChangesAsync(cts.Token);

                    var publisher = _publisherFactory.GetPublisher(target.ChannelType);
                    var result = await publisher.PublishAsync(target, publication, target.Channel, cts.Token);

                    if (result.Success)
                    {
                        target.Status = TargetStatus.Published;
                        target.PublishedAtUtc = DateTime.UtcNow;
                    }
                    else
                    {
                        target.Status = TargetStatus.Failed;
                        target.LastError = result.ErrorMessage;
                        target.RetryCount++;
                    }

                    await _context.SaveChangesAsync(cts.Token);
                }
                catch (TaskCanceledException)
                {
                    target.Status = TargetStatus.Failed;
                    target.LastError = "Request timeout";
                    target.RetryCount++;
                    await _context.SaveChangesAsync();
                }
                catch (OperationCanceledException)
                {
                    target.Status = TargetStatus.Failed;
                    target.LastError = "Operation canceled";
                    target.RetryCount++;
                    await _context.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    target.Status = TargetStatus.Failed;
                    target.LastError = ex.Message;
                    target.RetryCount++;
                    await _context.SaveChangesAsync();
                }
            });

            await Task.WhenAll(tasks);

            publication.Status = publication.Targets.All(t => t.Status == TargetStatus.Published) 
                ? PublicationStatus.Published 
                : PublicationStatus.Failed;
            publication.PublishedAtUtc = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Логируем ошибку, но не выбрасываем исключение, так как это фоновый процесс
            var logger = HttpContext.RequestServices.GetRequiredService<ILogger<PublicationsController>>();
            logger.LogError(ex, "Error publishing publication {PublicationId}", publicationId);
        }
    }
}

public class CreatePublicationDto
{
    public Guid ProjectId { get; set; }
    public string Text { get; set; } = string.Empty;
    public string? DeltaQuill { get; set; }
    public bool IsPublish { get; set; }
    public bool IsNow { get; set; }
    public bool IsLater { get; set; }
    public DateTime? ScheduledAtUtc { get; set; }
    public int? ClientTimezoneMinutes { get; set; }
    public List<CreatePublicationTargetDto> Targets { get; set; } = new();
    public List<Guid>? FileIds { get; set; }
}

public class CreatePublicationTargetDto
{
    public Guid ChannelId { get; set; }
    public string? CustomParamsJson { get; set; }
}

public class UpdatePublicationDto
{
    public string Text { get; set; } = string.Empty;
    public string? DeltaQuill { get; set; }
    public bool IsPublish { get; set; }
    public bool IsNow { get; set; }
    public bool IsLater { get; set; }
    public DateTime? ScheduledAtUtc { get; set; }
    public int? ClientTimezoneMinutes { get; set; }
}

