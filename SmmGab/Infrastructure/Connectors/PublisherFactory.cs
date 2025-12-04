using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SmmGab.Application.Abstractions;
using SmmGab.Domain.Enums;

namespace SmmGab.Infrastructure.Connectors;

public class PublisherFactory : IPublisherFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IDeltaFileExtractor _deltaFileExtractor;
    private readonly IFileStorageService _fileStorageService;
    private readonly ILoggerFactory _loggerFactory;

    public PublisherFactory(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IDeltaFileExtractor deltaFileExtractor,
        IFileStorageService fileStorageService,
        ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _deltaFileExtractor = deltaFileExtractor;
        _fileStorageService = fileStorageService;
        _loggerFactory = loggerFactory;
    }

    public IPublisher GetPublisher(ChannelType channelType)
    {
        var timeoutSeconds = int.TryParse(_configuration["Connectors:TimeoutSeconds"], out var timeout) ? timeout : 30;
        var httpClient = _httpClientFactory.CreateClient();
        httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

        return channelType switch
        {
            ChannelType.Vk => new VkPublisher(
                httpClient,
                _configuration,
                _deltaFileExtractor,
                _fileStorageService,
                _loggerFactory.CreateLogger<VkPublisher>()),
            ChannelType.Telegram => new TelegramPublisher(
                httpClient,
                _configuration,
                _deltaFileExtractor,
                _fileStorageService,
                _loggerFactory.CreateLogger<TelegramPublisher>()),
            _ => throw new NotSupportedException($"Channel type {channelType} is not supported")
        };
    }
}

