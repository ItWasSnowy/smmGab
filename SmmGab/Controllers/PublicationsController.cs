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
        
        // Получаем проект из сессии
        var selectedProjectId = HttpContext.Session.GetString("SelectedProjectId");
        if (string.IsNullOrEmpty(selectedProjectId) || !Guid.TryParse(selectedProjectId, out var projectId))
        {
            // Если проекта нет в сессии, редиректим на выбор проекта
            return RedirectToAction("Index", "Home");
        }

        // Проверяем, что проект существует и принадлежит пользователю
        var project = await _context.Projects
            .FirstOrDefaultAsync(p => p.Id == projectId && p.OwnerId == userId);

        if (project == null)
        {
            HttpContext.Session.Remove("SelectedProjectId");
            return RedirectToAction("Index", "Home");
        }

        // Получаем каналы только для текущего проекта
        ViewBag.Channels = await _context.Channels
            .Include(c => c.Project)
            .Where(c => c.ProjectId == projectId && c.Project.OwnerId == userId)
            .OrderBy(c => c.DisplayName)
            .ToListAsync();

        ViewBag.Project = project;

        var model = new CreatePublicationViewModel
        {
            ProjectId = projectId, // Устанавливаем проект из сессии
            IsPublish = true,
            IsNow = false,
            IsLater = false
        };

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreatePublicationViewModel model)
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);

        _logger.LogInformation("Creating publication. Text: {Text}, Body: {Body}, IsPublish: {IsPublish}, UploadedFileIds: {UploadedFileIds}, MediaFilesCount: {MediaFilesCount}", 
            model.Text, 
            string.IsNullOrEmpty(model.Body) ? "NULL/EMPTY" : $"Length: {model.Body.Length}, Preview: {model.Body.Substring(0, Math.Min(200, model.Body.Length))}...", 
            model.IsPublish,
            model.UploadedFileIds ?? "NULL",
            model.MediaFiles?.Count() ?? 0);

        // Получаем проект из сессии
        var selectedProjectId = HttpContext.Session.GetString("SelectedProjectId");
        if (string.IsNullOrEmpty(selectedProjectId) || !Guid.TryParse(selectedProjectId, out var sessionProjectId))
        {
            ModelState.AddModelError("", "Проект не выбран. Пожалуйста, выберите проект.");
            return RedirectToAction("Index", "Home");
        }

        // Используем проект из сессии, игнорируя model.ProjectId
        model.ProjectId = sessionProjectId;

        if (!ModelState.IsValid)
        {
            var projectForView = await _context.Projects
                .FirstOrDefaultAsync(p => p.Id == sessionProjectId && p.OwnerId == userId);

            if (projectForView != null)
            {
                ViewBag.Project = projectForView;
                ViewBag.Channels = await _context.Channels
                    .Include(c => c.Project)
                    .Where(c => c.ProjectId == sessionProjectId && c.Project.OwnerId == userId)
                    .OrderBy(c => c.DisplayName)
                    .ToListAsync();
            }

            return View(model);
        }

        var project = await _context.Projects
            .FirstOrDefaultAsync(p => p.Id == sessionProjectId && p.OwnerId == userId);

        if (project == null)
        {
            ModelState.AddModelError("", "Проект не найден");
            HttpContext.Session.Remove("SelectedProjectId");
            return RedirectToAction("Index", "Home");
        }

        var now = DateTime.UtcNow;
        DateTime? scheduledAt = model.IsNow ? now : (model.IsLater && model.ScheduledAtUtc.HasValue ? model.ScheduledAtUtc.Value.ToUniversalTime() : null);

        var publication = new Domain.Models.Publication
        {
            Id = Guid.NewGuid(),
            ProjectId = model.ProjectId,
            Text = model.Text,
            Body = model.Body, // Используем Body вместо DeltaQuill
            DeltaQuill = null, // Не используем DeltaQuill
            AuthorId = userId,
            CreatedAtUtc = now,
            IsPublish = model.IsPublish,
            IsNow = model.IsNow,
            IsLater = model.IsLater,
            ScheduledAtUtc = scheduledAt,
            ClientTimezoneMinutes = model.ClientTimezoneMinutes,
            Status = model.IsPublish ? (model.IsNow ? Domain.Enums.PublicationStatus.Scheduled : Domain.Enums.PublicationStatus.Scheduled) : Domain.Enums.PublicationStatus.Draft
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

        // Сохраняем публикацию
        _context.Publications.Add(publication);
        await _context.SaveChangesAsync();

        // Обрабатываем загруженные файлы
        var fileStorageService = HttpContext.RequestServices.GetRequiredService<IFileStorageService>();
        
        // Обрабатываем файлы, загруженные через AJAX (из UploadedFileIds)
        if (!string.IsNullOrEmpty(model.UploadedFileIds))
        {
            var fileIds = model.UploadedFileIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(id => Guid.TryParse(id, out var guid) ? guid : (Guid?)null)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .ToList();

            foreach (var fileId in fileIds)
            {
                var file = await fileStorageService.GetFileAsync(fileId, CancellationToken.None);
                if (file != null)
                {
                    file.PublicationId = publication.Id;
                    file.IsTemporary = false;
                }
            }
            await _context.SaveChangesAsync();
        }

        // Обрабатываем файлы, загруженные через форму
        if (model.MediaFiles != null && model.MediaFiles.Any())
        {
            foreach (var mediaFile in model.MediaFiles)
            {
                if (mediaFile.Length > 0)
                {
                    await using var stream = mediaFile.OpenReadStream();
                    var fileStorage = await fileStorageService.SaveFileAsync(
                        stream, 
                        mediaFile.FileName, 
                        mediaFile.ContentType, 
                        mediaFile.Length, 
                        CancellationToken.None);
                    
                    fileStorage.PublicationId = publication.Id;
                    fileStorage.IsTemporary = false;
                }
            }
            await _context.SaveChangesAsync();
        }

        // Сохраняем все изменения перед запуском публикации
        await _context.SaveChangesAsync();
        
        _logger.LogInformation("Publication {PublicationId} saved. Body length: {BodyLength}, Files count: {FilesCount}", 
            publication.Id,
            string.IsNullOrEmpty(publication.Body) ? 0 : publication.Body.Length,
            await _context.FileStorage.CountAsync(f => f.PublicationId == publication.Id));

        // Если немедленная публикация - запускаем публикацию
        if (publication.IsNow)
        {
            _logger.LogInformation("Starting immediate publication for publication {PublicationId}", publication.Id);
            // Небольшая задержка, чтобы убедиться, что все изменения сохранены
            await Task.Delay(200);
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

        // Редиректим на Dashboard проекта
        return RedirectToAction("Dashboard", "Home", new { id = publication.ProjectId });
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

            _logger.LogInformation("Found publication {PublicationId} with {TargetCount} targets, Body length: {BodyLength}, Files count: {FilesCount}", 
                publicationId, 
                publication.Targets.Count,
                string.IsNullOrEmpty(publication.Body) ? 0 : publication.Body.Length,
                publication.Files?.Count ?? 0);

            publication.Status = Domain.Enums.PublicationStatus.Publishing;
            await context.SaveChangesAsync();

            var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

            var tasks = publication.Targets.Select(async target =>
            {
                // Создаем отдельный scope для каждой задачи, чтобы избежать конфликтов с DbContext
                using var targetScope = _serviceScopeFactory.CreateScope();
                var targetContext = targetScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var targetPublisherFactory = targetScope.ServiceProvider.GetRequiredService<IPublisherFactory>();
                
                try
                {
                    _logger.LogInformation("Publishing target {TargetId} to channel {ChannelId} ({ChannelType})", 
                        target.Id, target.ChannelId, target.ChannelType);

                    // Перезагружаем target из нового контекста
                    var targetEntity = await targetContext.PublicationTargets
                        .Include(t => t.Channel)
                        .FirstOrDefaultAsync(t => t.Id == target.Id, cts.Token);
                    
                    if (targetEntity == null)
                    {
                        _logger.LogWarning("Target {TargetId} not found in database", target.Id);
                        return;
                    }

                    targetEntity.Status = Domain.Enums.TargetStatus.Publishing;
                    await targetContext.SaveChangesAsync(cts.Token);

                    // Перезагружаем publication из нового контекста для публикации
                    var publicationForPublish = await targetContext.Publications
                        .Include(p => p.Files)
                        .FirstOrDefaultAsync(p => p.Id == publicationId, cts.Token);
                    
                    if (publicationForPublish == null)
                    {
                        _logger.LogWarning("Publication {PublicationId} not found in database", publicationId);
                        return;
                    }
                    
                    _logger.LogInformation("Target {TargetId} - Loaded publication with {FilesCount} files, Body length: {BodyLength}", 
                        targetEntity.Id,
                        publicationForPublish.Files?.Count ?? 0,
                        string.IsNullOrEmpty(publicationForPublish.Body) ? 0 : publicationForPublish.Body.Length);

                    var publisher = targetPublisherFactory.GetPublisher(targetEntity.ChannelType);
                    _logger.LogDebug("Got publisher for channel type {ChannelType}", targetEntity.ChannelType);

                    var result = await publisher.PublishAsync(targetEntity, publicationForPublish, targetEntity.Channel, cts.Token);

                    if (result.Success)
                    {
                        _logger.LogInformation("Successfully published target {TargetId} to channel {ChannelId}", targetEntity.Id, targetEntity.ChannelId);
                        targetEntity.Status = Domain.Enums.TargetStatus.Published;
                        targetEntity.PublishedAtUtc = DateTime.UtcNow;
                    }
                    else
                    {
                        _logger.LogError("Failed to publish target {TargetId} to channel {ChannelId}: {Error}", 
                            targetEntity.Id, targetEntity.ChannelId, result.ErrorMessage);
                        targetEntity.Status = Domain.Enums.TargetStatus.Failed;
                        targetEntity.LastError = result.ErrorMessage;
                        targetEntity.RetryCount++;
                    }

                    await targetContext.SaveChangesAsync(cts.Token);
                }
                catch (TaskCanceledException ex)
                {
                    _logger.LogWarning(ex, "Request timeout for target {TargetId}", target.Id);
                    await UpdateTargetStatusAsync(targetContext, target.Id, Domain.Enums.TargetStatus.Failed, "Request timeout", cts.Token);
                }
                catch (OperationCanceledException ex)
                {
                    _logger.LogWarning(ex, "Operation canceled for target {TargetId}", target.Id);
                    await UpdateTargetStatusAsync(targetContext, target.Id, Domain.Enums.TargetStatus.Failed, "Operation canceled", cts.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Exception publishing target {TargetId} to channel {ChannelId}", target.Id, target.ChannelId);
                    await UpdateTargetStatusAsync(targetContext, target.Id, Domain.Enums.TargetStatus.Failed, ex.Message, cts.Token);
                }
            });

            await Task.WhenAll(tasks);

            // Небольшая задержка, чтобы убедиться, что все изменения сохранены в БД
            await Task.Delay(1000);

            // Обновляем статус публикации в основном контексте
            // Перезагружаем из БД, чтобы получить актуальные статусы targets
            // Используем retry для случая, если targets еще не сохранились
            List<Domain.Models.PublicationTarget> targets = new List<Domain.Models.PublicationTarget>();
            int retryCount = 0;
            const int maxRetries = 10; // Увеличиваем количество попыток
            
            while (retryCount < maxRetries)
            {
                // Перезагружаем targets из БД с AsNoTracking чтобы получить актуальные данные
                targets = await context.PublicationTargets
                    .AsNoTracking()
                    .Where(t => t.PublicationId == publicationId)
                    .ToListAsync();
                
                if (targets.Count == 0)
                {
                    _logger.LogWarning("No targets found for publication {PublicationId}, retrying... (attempt {Attempt}/{MaxRetries})", 
                        publicationId, retryCount + 1, maxRetries);
                    if (retryCount < maxRetries - 1)
                    {
                        await Task.Delay(500);
                        retryCount++;
                        continue;
                    }
                    else
                    {
                        break;
                    }
                }
                
                // Проверяем статусы targets
                var publishedCount = targets.Count(t => t.Status == Domain.Enums.TargetStatus.Published);
                var failedCount = targets.Count(t => t.Status == Domain.Enums.TargetStatus.Failed);
                var publishingCount = targets.Count(t => t.Status == Domain.Enums.TargetStatus.Publishing);
                var scheduledCount = targets.Count(t => t.Status == Domain.Enums.TargetStatus.Scheduled);
                
                _logger.LogInformation("Attempt {Attempt}: Published={PublishedCount}, Failed={FailedCount}, Publishing={PublishingCount}, Scheduled={ScheduledCount}, Total={TotalCount}", 
                    retryCount + 1, publishedCount, failedCount, publishingCount, scheduledCount, targets.Count);
                
                // Если все targets Published или Failed (и нет Publishing/Scheduled) - выходим
                if (publishedCount + failedCount == targets.Count && targets.Count > 0 && publishingCount == 0 && scheduledCount == 0)
                {
                    _logger.LogInformation("All targets have final status, proceeding with status update");
                    break;
                }
                
                // Если еще есть targets в статусе Publishing или Scheduled - ждем еще
                if (retryCount < maxRetries - 1 && (publishingCount > 0 || scheduledCount > 0))
                {
                    _logger.LogDebug("Waiting for targets to complete publishing... (attempt {Attempt}/{MaxRetries})", 
                        retryCount + 1, maxRetries);
                    await Task.Delay(500);
                    retryCount++;
                }
                else
                {
                    break;
                }
            }

            _logger.LogInformation("Publication {PublicationId} status check: TargetsCount={TargetsCount}, Targets: {Targets}", 
                publicationId, targets.Count, string.Join(", ", targets.Select(t => $"{t.Id}: {t.Status}")));

            // Перезагружаем publication для обновления
            var publicationToUpdate = await context.Publications.FindAsync(publicationId);
            if (publicationToUpdate == null)
            {
                _logger.LogWarning("Publication {PublicationId} not found for update", publicationId);
                return;
            }

            var allPublished = targets.Count > 0 && targets.All(t => t.Status == Domain.Enums.TargetStatus.Published);
            var anyFailed = targets.Any(t => t.Status == Domain.Enums.TargetStatus.Failed);
            var anyPublishing = targets.Any(t => t.Status == Domain.Enums.TargetStatus.Publishing);

            if (allPublished)
            {
                publicationToUpdate.Status = Domain.Enums.PublicationStatus.Published;
                publicationToUpdate.PublishedAtUtc = DateTime.UtcNow;
                _logger.LogInformation("Publication {PublicationId} marked as Published", publicationId);
            }
            else if (anyFailed && !anyPublishing)
            {
                // Если есть ошибки и нет публикаций в процессе - помечаем как Failed
                publicationToUpdate.Status = Domain.Enums.PublicationStatus.Failed;
                _logger.LogInformation("Publication {PublicationId} marked as Failed", publicationId);
            }
            else if (anyPublishing)
            {
                // Если еще есть публикации в процессе - оставляем Publishing
                publicationToUpdate.Status = Domain.Enums.PublicationStatus.Publishing;
                _logger.LogDebug("Publication {PublicationId} still Publishing", publicationId);
            }
            else
            {
                // Если нет targets или все в неопределенном состоянии - помечаем как Failed
                publicationToUpdate.Status = Domain.Enums.PublicationStatus.Failed;
                _logger.LogWarning("Publication {PublicationId} has no valid targets, marking as Failed", publicationId);
            }

            await context.SaveChangesAsync();

            _logger.LogInformation("Publication {PublicationId} completed with status {Status}", publicationId, publicationToUpdate.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error publishing publication {PublicationId}", publicationId);
        }
    }

    private async Task UpdateTargetStatusAsync(ApplicationDbContext context, Guid targetId, Domain.Enums.TargetStatus status, string errorMessage, CancellationToken cancellationToken)
    {
        try
        {
            var targetEntity = await context.PublicationTargets
                .FirstOrDefaultAsync(t => t.Id == targetId, cancellationToken);
            
            if (targetEntity != null)
            {
                targetEntity.Status = status;
                targetEntity.LastError = errorMessage;
                targetEntity.RetryCount++;
                await context.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update target status for target {TargetId}", targetId);
        }
    }
}

public class CreatePublicationViewModel
{
    public Guid ProjectId { get; set; } // Проект берется из сессии, не требуется валидация

    [Required(ErrorMessage = "Введите название публикации")]
    [StringLength(500, ErrorMessage = "Название должно быть не более 500 символов")]
    public string Text { get; set; } = string.Empty;

    [Display(Name = "Текст публикации")]
    public string? Body { get; set; } // Простой текст публикации (без форматирования)

    public string? DeltaQuill { get; set; } // Оставляем для обратной совместимости, но не используем

    public bool IsPublish { get; set; } = true;
    public bool IsNow { get; set; } = false;
    public bool IsLater { get; set; } = false;

    [Display(Name = "Запланировать на")]
    public DateTime? ScheduledAtUtc { get; set; }

    public int? ClientTimezoneMinutes { get; set; }

    [Display(Name = "Каналы для публикации")]
    public List<Guid>? ChannelIds { get; set; }

    [Display(Name = "Медиа файлы")]
    public List<IFormFile>? MediaFiles { get; set; }

    [Display(Name = "Загруженные файлы")]
    public string? UploadedFileIds { get; set; }
}

