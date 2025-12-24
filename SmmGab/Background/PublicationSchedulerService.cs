using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using SmmGab.Application.Abstractions;
using SmmGab.Data;
using SmmGab.Domain.Enums;

namespace SmmGab.Background;

public class PublicationSchedulerService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PublicationSchedulerService> _logger;
    private readonly RetryOptions _retryOptions;
    private readonly SemaphoreSlim _semaphore = new(10); // Максимум 10 параллельных публикаций

    public PublicationSchedulerService(
        IServiceProvider serviceProvider,
        ILogger<PublicationSchedulerService> logger,
        IOptions<RetryOptions> retryOptions)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _retryOptions = retryOptions.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Ждем, пока база данных будет готова
        await WaitForDatabaseReadyAsync(stoppingToken);

        // Загружаем существующие задачи при запуске
        try
        {
            await LoadPendingPublicationsAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load pending publications on startup, will retry later");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPublicationsAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); // Проверяем каждые 5 секунд
            }
            catch (TaskCanceledException)
            {
                // Игнорируем отмену задачи
                break;
            }
            catch (OperationCanceledException)
            {
                // Игнорируем отмену операции
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in publication scheduler");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    private async Task WaitForDatabaseReadyAsync(CancellationToken cancellationToken)
    {
        var maxRetries = 30; // Максимум 30 попыток (5 минут)
        var retryCount = 0;

        while (retryCount < maxRetries && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                
                // Пробуем выполнить простой запрос для проверки готовности БД
                var canConnect = await context.Database.CanConnectAsync(cancellationToken);
                if (!canConnect)
                {
                    retryCount++;
                    _logger.LogWarning("Cannot connect to database, retry {RetryCount}/{MaxRetries}", retryCount, maxRetries);
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                    continue;
                }
                
                // Проверяем, что таблицы существуют, пытаясь выполнить простой запрос
                try
                {
                    await context.Database.ExecuteSqlRawAsync(
                        "SELECT 1 FROM \"Publications\" LIMIT 1",
                        cancellationToken);
                    
                    _logger.LogInformation("Database is ready");
                    return;
                }
                catch (PostgresException ex) when (ex.SqlState == "42P01") // Таблица не существует
                {
                    retryCount++;
                    _logger.LogWarning("Database tables do not exist yet, retry {RetryCount}/{MaxRetries}", retryCount, maxRetries);
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                    continue;
                }
            }
            catch (Exception ex)
            {
                retryCount++;
                _logger.LogWarning(ex, "Database not ready yet, retry {RetryCount}/{MaxRetries}", retryCount, maxRetries);
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
        }

        if (retryCount >= maxRetries)
        {
            _logger.LogError("Database was not ready after {MaxRetries} attempts", maxRetries);
        }
    }

    private async Task LoadPendingPublicationsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Проверяем, что таблицы существуют
            var canConnect = await context.Database.CanConnectAsync(cancellationToken);
            if (!canConnect)
            {
                _logger.LogWarning("Cannot connect to database");
                return;
            }

            var now = DateTime.UtcNow;
            var pendingPublications = await context.Publications
                .Include(p => p.Targets)
                .ThenInclude(t => t.Channel)
                .Where(p => p.Status == PublicationStatus.Scheduled &&
                           p.ScheduledAtUtc.HasValue &&
                           p.ScheduledAtUtc <= now)
                .ToListAsync(cancellationToken);

            _logger.LogInformation("Loaded {Count} pending publications", pendingPublications.Count);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01") // Таблица не существует
        {
            _logger.LogWarning("Database tables do not exist yet, skipping load");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading pending publications");
            throw;
        }
    }

    private async Task ProcessPublicationsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var publisherFactory = scope.ServiceProvider.GetRequiredService<IPublisherFactory>();

            // Проверяем подключение к БД
            if (!await context.Database.CanConnectAsync(cancellationToken))
            {
                _logger.LogWarning("Cannot connect to database");
                return;
            }

            var now = DateTime.UtcNow;
            var publications = await context.Publications
                .Include(p => p.Targets)
                .ThenInclude(t => t.Channel)
                .Include(p => p.Files)
                .Where(p => p.Status == PublicationStatus.Scheduled &&
                           p.ScheduledAtUtc.HasValue &&
                           p.ScheduledAtUtc <= now)
                .Take(10)
                .ToListAsync(cancellationToken);

            if (publications.Count == 0)
                return;

            _logger.LogInformation("Found {Count} scheduled publications ready to process", publications.Count);

            // Обрабатываем публикации последовательно, чтобы избежать конфликтов DbContext
            foreach (var pub in publications)
            {
                await ProcessPublicationAsync(pub, context, publisherFactory, cancellationToken);
            }
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01") // Таблица не существует
        {
            _logger.LogWarning("Database tables do not exist yet");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing publications");
            throw;
        }
    }

    private async Task ProcessPublicationAsync(
        Domain.Models.Publication publication,
        ApplicationDbContext context,
        IPublisherFactory publisherFactory,
        CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            publication.Status = PublicationStatus.Publishing;
            await context.SaveChangesAsync(cancellationToken);

            // Обрабатываем цели последовательно, чтобы не писать в один DbContext из разных потоков
            foreach (var target in publication.Targets)
            {
                await PublishTargetAsync(target, publication, publisherFactory, context, cancellationToken);
            }

            publication.Status = publication.Targets.All(t => t.Status == TargetStatus.Published)
                ? PublicationStatus.Published
                : PublicationStatus.Failed;
            publication.PublishedAtUtc = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing publication {PublicationId}", publication.Id);
            publication.Status = PublicationStatus.Failed;
            await context.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task PublishTargetAsync(
        Domain.Models.PublicationTarget target,
        Domain.Models.Publication publication,
        IPublisherFactory publisherFactory,
        ApplicationDbContext context,
        CancellationToken cancellationToken)
    {
        if (target.Status == TargetStatus.Published || target.Status == TargetStatus.Skipped)
        {
            _logger.LogDebug("Target {TargetId} already published or skipped, skipping", target.Id);
            return;
        }

        // Если публикация уже обрабатывается или опубликована, пропускаем
        if (publication.Status == PublicationStatus.Published || publication.Status == PublicationStatus.Publishing)
        {
            _logger.LogDebug("Publication {PublicationId} already published or publishing, skipping target {TargetId}", publication.Id, target.Id);
            return;
        }

        if (target.RetryCount >= _retryOptions.MaxRetryCount)
        {
            target.Status = TargetStatus.Failed;
            target.LastError = "Maximum retry count exceeded";
            await context.SaveChangesAsync(cancellationToken);
            return;
        }

        try
        {
            _logger.LogInformation("Publishing target {TargetId} to channel {ChannelId} ({ChannelType}) for publication {PublicationId}", 
                target.Id, target.ChannelId, target.ChannelType, publication.Id);

            target.Status = TargetStatus.Publishing;
            await context.SaveChangesAsync(cancellationToken);

            var publisher = publisherFactory.GetPublisher(target.ChannelType);
            _logger.LogDebug("Got publisher for channel type {ChannelType}", target.ChannelType);

            var result = await publisher.PublishAsync(target, publication, target.Channel, cancellationToken);

            if (result.Success)
            {
                _logger.LogInformation("Successfully published target {TargetId} to channel {ChannelId}", target.Id, target.ChannelId);
                target.Status = TargetStatus.Published;
                target.PublishedAtUtc = DateTime.UtcNow;
                target.LastError = null;
            }
            else
            {
                _logger.LogError("Failed to publish target {TargetId} to channel {ChannelId}: {Error}", 
                    target.Id, target.ChannelId, result.ErrorMessage);
                target.LastError = result.ErrorMessage;
                target.RetryCount++;

                if (result.IsPermanentError)
                {
                    _logger.LogWarning("Permanent error for target {TargetId}, marking as failed", target.Id);
                    target.Status = TargetStatus.Failed;
                }
                else
                {
                    // Временная ошибка - планируем повторную попытку
                    _logger.LogInformation("Temporary error for target {TargetId}, scheduling retry", target.Id);
                    target.Status = TargetStatus.Scheduled;
                    var delay = CalculateRetryDelay(target.RetryCount);
                    await Task.Delay(delay, cancellationToken);
                }
            }

            await context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error publishing target {TargetId}", target.Id);
            target.RetryCount++;
            target.LastError = ex.Message;

            if (target.RetryCount >= _retryOptions.MaxRetryCount)
            {
                target.Status = TargetStatus.Failed;
            }
            else
            {
                target.Status = TargetStatus.Scheduled;
            }

            await context.SaveChangesAsync(cancellationToken);
        }
    }

    private TimeSpan CalculateRetryDelay(int retryCount)
    {
        var baseDelay = TimeSpan.FromSeconds(_retryOptions.BaseDelaySeconds);
        var exponentialDelay = TimeSpan.FromSeconds(_retryOptions.BaseDelaySeconds * Math.Pow(2, retryCount - 1));
        var maxDelay = TimeSpan.FromMinutes(_retryOptions.MaxDelayMinutes);

        var delay = exponentialDelay > maxDelay ? maxDelay : exponentialDelay;
        return delay;
    }
}

public class RetryOptions
{
    public int MaxRetryCount { get; set; } = 3;
    public int BaseDelaySeconds { get; set; } = 2;
    public int MaxDelayMinutes { get; set; } = 1;
}

