using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmmGab.Application.Abstractions;
using SmmGab.Data;
using SmmGab.Domain.Enums;

namespace SmmGab.Controllers;

[Authorize]
public class PublicationsController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IPublisherFactory _publisherFactory;
    private readonly ILogger<PublicationsController> _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public PublicationsController(
        ApplicationDbContext context, 
        IPublisherFactory publisherFactory, 
        ILogger<PublicationsController> logger,
        IServiceScopeFactory serviceScopeFactory)
    {
        _context = context;
        _publisherFactory = publisherFactory;
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
    }

    public async Task<IActionResult> Index([FromQuery] Guid? projectId, [FromQuery] PublicationStatus? status)
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        
        // Если projectId не передан, берем из сессии
        if (!projectId.HasValue)
        {
            var sessionProjectId = HttpContext.Session.GetString("SelectedProjectId");
            if (!string.IsNullOrEmpty(sessionProjectId) && Guid.TryParse(sessionProjectId, out var sessionId))
            {
                projectId = sessionId;
            }
        }
        
        var query = _context.Publications
            .Include(p => p.Project)
            .Include(p => p.Targets)
            .ThenInclude(t => t.Channel)
            .Where(p => p.Project.OwnerId == userId);

        if (projectId.HasValue)
            query = query.Where(p => p.ProjectId == projectId.Value);

        if (status.HasValue)
            query = query.Where(p => p.Status == status.Value);

        var publications = await query
            .OrderByDescending(p => p.CreatedAtUtc)
            .ToListAsync();

        ViewBag.Projects = await _context.Projects
            .Where(p => p.OwnerId == userId)
            .OrderBy(p => p.Name)
            .ToListAsync();

        return View(publications);
    }

    public async Task<IActionResult> Calendar([FromQuery] int? year, [FromQuery] int? month)
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        
        var now = DateTime.UtcNow;
        var currentYear = year ?? now.Year;
        var currentMonth = month ?? now.Month;
        
        var startDate = new DateTime(currentYear, currentMonth, 1, 0, 0, 0, DateTimeKind.Utc);
        var endDate = startDate.AddMonths(1);

        var publications = await _context.Publications
            .Include(p => p.Project)
            .Include(p => p.Targets)
            .ThenInclude(t => t.Channel)
            .Where(p => p.Project.OwnerId == userId &&
                       p.ScheduledAtUtc.HasValue &&
                       p.ScheduledAtUtc >= startDate &&
                       p.ScheduledAtUtc < endDate)
            .OrderBy(p => p.ScheduledAtUtc)
            .ToListAsync();

        ViewBag.Year = currentYear;
        ViewBag.Month = currentMonth;
        ViewBag.StartDate = startDate;
        ViewBag.EndDate = endDate;

        return View(publications);
    }

    [HttpGet]
    public async Task<IActionResult> Create()
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        
        ViewBag.Projects = await _context.Projects
            .Where(p => p.OwnerId == userId)
            .OrderBy(p => p.Name)
            .ToListAsync();

        ViewBag.Channels = await _context.Channels
            .Include(c => c.Project)
            .Where(c => c.Project.OwnerId == userId)
            .OrderBy(c => c.DisplayName)
            .ToListAsync();

        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreatePublicationViewModel model)
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);

        if (!ModelState.IsValid)
        {
            ViewBag.Projects = await _context.Projects
                .Where(p => p.OwnerId == userId)
                .OrderBy(p => p.Name)
                .ToListAsync();

            ViewBag.Channels = await _context.Channels
                .Include(c => c.Project)
                .Where(c => c.Project.OwnerId == userId)
                .OrderBy(c => c.DisplayName)
                .ToListAsync();

            return View(model);
        }

        var project = await _context.Projects
            .FirstOrDefaultAsync(p => p.Id == model.ProjectId && p.OwnerId == userId);

        if (project == null)
        {
            ModelState.AddModelError("", "Проект не найден");
            ViewBag.Projects = await _context.Projects
                .Where(p => p.OwnerId == userId)
                .OrderBy(p => p.Name)
                .ToListAsync();

            ViewBag.Channels = await _context.Channels
                .Include(c => c.Project)
                .Where(c => c.Project.OwnerId == userId)
                .OrderBy(c => c.DisplayName)
                .ToListAsync();

            return View(model);
        }

        var now = DateTime.UtcNow;
        DateTime? scheduledAt = model.IsNow ? now : (model.IsLater && model.ScheduledAtUtc.HasValue ? model.ScheduledAtUtc.Value.ToUniversalTime() : null);

        var publication = new Domain.Models.Publication
        {
            Id = Guid.NewGuid(),
            ProjectId = model.ProjectId,
            Text = model.Text,
            DeltaQuill = model.DeltaQuill,
            AuthorId = userId,
            CreatedAtUtc = now,
            IsPublish = model.IsPublish,
            IsNow = model.IsNow,
            IsLater = model.IsLater,
            ScheduledAtUtc = scheduledAt,
            ClientTimezoneMinutes = model.ClientTimezoneMinutes,
            Status = model.IsNow ? Domain.Enums.PublicationStatus.Scheduled : (model.IsPublish ? Domain.Enums.PublicationStatus.Scheduled : Domain.Enums.PublicationStatus.Draft)
        };

        // Создаем цели публикации
        if (model.ChannelIds != null && model.ChannelIds.Any())
        {
            var channels = await _context.Channels
                .Include(c => c.Project)
                .Where(c => model.ChannelIds.Contains(c.Id) && c.Project.OwnerId == userId)
                .ToListAsync();

            foreach (var channel in channels)
            {
                var target = new Domain.Models.PublicationTarget
                {
                    Id = Guid.NewGuid(),
                    PublicationId = publication.Id,
                    ChannelId = channel.Id,
                    ChannelType = channel.Type,
                    Status = publication.IsNow ? Domain.Enums.TargetStatus.Scheduled : Domain.Enums.TargetStatus.Scheduled,
                    RetryCount = 0
                };

                publication.Targets.Add(target);
            }
        }

        _context.Publications.Add(publication);
        await _context.SaveChangesAsync();

        // Если немедленная публикация - запускаем публикацию
        if (publication.IsNow)
        {
            _logger.LogInformation("Starting immediate publication for publication {PublicationId}", publication.Id);
            _ = Task.Run(async () =>
            {
                try
                {
                    await PublishPublicationAsync(publication.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled exception in Task.Run for publication {PublicationId}", publication.Id);
                }
            });
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Details(Guid id)
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

        return View(publication);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PublishNow(Guid id)
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        var publication = await _context.Publications
            .Include(p => p.Project)
            .FirstOrDefaultAsync(p => p.Id == id && p.Project.OwnerId == userId);

        if (publication == null)
            return NotFound();

        publication.IsNow = true;
        publication.Status = Domain.Enums.PublicationStatus.Scheduled;
        publication.ScheduledAtUtc = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        _logger.LogInformation("Starting manual publication for publication {PublicationId}", publication.Id);
        _ = Task.Run(async () =>
        {
            try
            {
                await PublishPublicationAsync(publication.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in Task.Run for publication {PublicationId}", publication.Id);
            }
        });

        return RedirectToAction(nameof(Details), new { id = publication.Id });
    }

    private async Task PublishPublicationAsync(Guid publicationId)
    {
        try
        {
            _logger.LogInformation("PublishPublicationAsync started for publication {PublicationId}", publicationId);

            // Создаем новый scope для фонового потока, так как контекст из HTTP-запроса будет освобожден
            using var scope = _serviceScopeFactory.CreateScope();
            _logger.LogDebug("Created new scope for publication {PublicationId}", publicationId);

            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var publisherFactory = scope.ServiceProvider.GetRequiredService<IPublisherFactory>();
            _logger.LogDebug("Retrieved services from scope for publication {PublicationId}", publicationId);

            _logger.LogInformation("Publishing publication {PublicationId}", publicationId);

            var publication = await context.Publications
                .Include(p => p.Targets)
                .ThenInclude(t => t.Channel)
                .Include(p => p.Files)
                .FirstOrDefaultAsync(p => p.Id == publicationId);

            if (publication == null)
            {
                _logger.LogWarning("Publication {PublicationId} not found", publicationId);
                return;
            }

            _logger.LogInformation("Found publication {PublicationId} with {TargetCount} targets", publicationId, publication.Targets.Count);

            publication.Status = Domain.Enums.PublicationStatus.Publishing;
            await context.SaveChangesAsync();

            var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

            var tasks = publication.Targets.Select(async target =>
            {
                try
                {
                    _logger.LogInformation("Publishing target {TargetId} to channel {ChannelId} ({ChannelType})", 
                        target.Id, target.ChannelId, target.ChannelType);

                    target.Status = Domain.Enums.TargetStatus.Publishing;
                    await context.SaveChangesAsync(cts.Token);

                    var publisher = publisherFactory.GetPublisher(target.ChannelType);
                    _logger.LogDebug("Got publisher for channel type {ChannelType}", target.ChannelType);

                    var result = await publisher.PublishAsync(target, publication, target.Channel, cts.Token);

                    if (result.Success)
                    {
                        _logger.LogInformation("Successfully published target {TargetId} to channel {ChannelId}", target.Id, target.ChannelId);
                        target.Status = Domain.Enums.TargetStatus.Published;
                        target.PublishedAtUtc = DateTime.UtcNow;
                    }
                    else
                    {
                        _logger.LogError("Failed to publish target {TargetId} to channel {ChannelId}: {Error}", 
                            target.Id, target.ChannelId, result.ErrorMessage);
                        target.Status = Domain.Enums.TargetStatus.Failed;
                        target.LastError = result.ErrorMessage;
                        target.RetryCount++;
                    }

                    await context.SaveChangesAsync(cts.Token);
                }
                catch (TaskCanceledException ex)
                {
                    _logger.LogWarning(ex, "Request timeout for target {TargetId}", target.Id);
                    target.Status = Domain.Enums.TargetStatus.Failed;
                    target.LastError = "Request timeout";
                    target.RetryCount++;
                    await context.SaveChangesAsync();
                }
                catch (OperationCanceledException ex)
                {
                    _logger.LogWarning(ex, "Operation canceled for target {TargetId}", target.Id);
                    target.Status = Domain.Enums.TargetStatus.Failed;
                    target.LastError = "Operation canceled";
                    target.RetryCount++;
                    await context.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Exception publishing target {TargetId} to channel {ChannelId}", target.Id, target.ChannelId);
                    target.Status = Domain.Enums.TargetStatus.Failed;
                    target.LastError = ex.Message;
                    target.RetryCount++;
                    await context.SaveChangesAsync();
                }
            });

            await Task.WhenAll(tasks);

            var allPublished = publication.Targets.All(t => t.Status == Domain.Enums.TargetStatus.Published);
            publication.Status = allPublished
                ? Domain.Enums.PublicationStatus.Published
                : Domain.Enums.PublicationStatus.Failed;
            publication.PublishedAtUtc = DateTime.UtcNow;
            await context.SaveChangesAsync();

            _logger.LogInformation("Publication {PublicationId} completed with status {Status}", publicationId, publication.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error publishing publication {PublicationId}", publicationId);
        }
    }
}

public class CreatePublicationViewModel
{
    [Required(ErrorMessage = "Выберите проект")]
    public Guid ProjectId { get; set; }

    [Required(ErrorMessage = "Введите название публикации")]
    [StringLength(500, ErrorMessage = "Название должно быть не более 500 символов")]
    public string Text { get; set; } = string.Empty;

    public string? DeltaQuill { get; set; }

    public bool IsPublish { get; set; } = true;
    public bool IsNow { get; set; } = false;
    public bool IsLater { get; set; } = false;

    [Display(Name = "Запланировать на")]
    public DateTime? ScheduledAtUtc { get; set; }

    public int? ClientTimezoneMinutes { get; set; }

    [Display(Name = "Каналы для публикации")]
    public List<Guid>? ChannelIds { get; set; }
}

