using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SmmGab.Application.Abstractions;
using SmmGab.Domain.Enums;
using SmmGab.Domain.Models;

namespace SmmGab.Infrastructure.Connectors;

public class VkPublisher : IPublisher
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly IDeltaFileExtractor _deltaFileExtractor;
    private readonly IFileStorageService _fileStorageService;
    private readonly ILogger<VkPublisher> _logger;
    private readonly string _apiVersion;

    public VkPublisher(
        HttpClient httpClient,
        IConfiguration configuration,
        IDeltaFileExtractor deltaFileExtractor,
        IFileStorageService fileStorageService,
        ILogger<VkPublisher> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _deltaFileExtractor = deltaFileExtractor;
        _fileStorageService = fileStorageService;
        _logger = logger;
        _apiVersion = _configuration["Connectors:VkApiVersion"] ?? "5.199";
    }

    public async Task<PublishResult> PublishAsync(
        PublicationTarget target,
        Publication publication,
        Channel channel,
        CancellationToken cancellationToken)
    {
        try
        {
            // Получаем токен
            var token = GetToken(channel, target);
            if (string.IsNullOrEmpty(token))
            {
                _logger.LogError("VK token not found for channel {ChannelId}", channel.Id);
                return new PublishResult
                {
                    Success = false,
                    IsPermanentError = true,
                    ErrorMessage = "VK token not found"
                };
            }

            var ownerId = channel.ExternalId;
            _logger.LogInformation("Publishing to VK group {GroupId} (ChannelId: {ChannelId})", ownerId, channel.Id);

            // Получаем файлы, связанные с публикацией через PublicationId
            var filesFromPublication = publication.Files?.Where(f => !f.IsTemporary).ToList() ?? new List<FileStorage>();
            
            _logger.LogInformation("VK Publisher - Publication files: {FileCount}, Body: {BodyLength}, Files collection: {FilesCollection}", 
                filesFromPublication.Count, 
                string.IsNullOrEmpty(publication.Body) ? "NULL/EMPTY" : publication.Body.Length.ToString(),
                publication.Files == null ? "NULL" : $"{publication.Files.Count} items");
            
            var images = filesFromPublication.Where(f => f.Type == FileType.Image).ToList();
            
            _logger.LogInformation("VK Publisher - Found {ImageCount} images in publication. File IDs: {FileIds}", 
                images.Count, 
                string.Join(", ", images.Select(f => f.Id)));

            if (images.Count > 10)
            {
                _logger.LogWarning("Too many images: {ImageCount}, maximum is 10", images.Count);
                return new PublishResult
                {
                    Success = false,
                    IsPermanentError = true,
                    ErrorMessage = "Maximum 10 images allowed"
                };
            }

            // Загружаем изображения и получаем attachment IDs
            var attachmentIds = new List<string>();
            foreach (var image in images)
            {
                _logger.LogDebug("Uploading image {ImageId} to VK", image.Id);
                var attachmentId = await UploadImageAsync(token, ownerId, image, cancellationToken);
                if (!string.IsNullOrEmpty(attachmentId))
                {
                    attachmentIds.Add(attachmentId);
                    _logger.LogDebug("Image uploaded successfully, attachment: {AttachmentId}", attachmentId);
                }
                else
                {
                    _logger.LogWarning("Failed to upload image {ImageId}", image.Id);
                }
            }

            // Формируем сообщение для VK: заголовок + текст публикации
            var message = BuildVkMessage(publication.Text, publication.Body);
            
            _logger.LogInformation("VK Publisher - Message: Title={Title}, Body={Body}, Message length={MessageLength}", 
                publication.Text, 
                string.IsNullOrEmpty(publication.Body) ? "NULL/EMPTY" : publication.Body.Substring(0, Math.Min(100, publication.Body.Length)) + "...",
                message.Length);
            var attachments = string.Join(",", attachmentIds);

            _logger.LogDebug("Publishing post to VK: owner_id={OwnerId}, message_length={MessageLength}, message={Message}, attachments_count={AttachmentsCount}", 
                ownerId, message.Length, message, attachmentIds.Count);

            var postUrl = $"https://api.vk.com/method/wall.post?access_token={token}&owner_id={ownerId}&message={Uri.EscapeDataString(message)}&attachments={Uri.EscapeDataString(attachments)}&from_group=1&v={_apiVersion}";

            var response = await _httpClient.GetAsync(postUrl, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            _logger.LogDebug("VK API response: {Response}", content);

            var result = JsonSerializer.Deserialize<JsonElement>(content);
            if (result.TryGetProperty("error", out var error))
            {
                var errorMsg = "Unknown error";
                var errorCode = 0;
                
                if (error.TryGetProperty("error_msg", out var msg))
                    errorMsg = msg.GetString() ?? "Unknown error";
                
                if (error.TryGetProperty("error_code", out var code))
                    errorCode = code.GetInt32();

                _logger.LogError("VK API error: Code={ErrorCode}, Message={ErrorMessage}, OwnerId={OwnerId}", errorCode, errorMsg, ownerId);

                // Определяем, постоянная ли это ошибка
                var isPermanent = errorCode switch
                {
                    5 => true,   // User authorization failed
                    6 => false,  // Too many requests per second (rate limit)
                    7 => true,   // Permission denied
                    8 => true,   // Invalid request
                    9 => true,   // Flood control
                    10 => true,  // Internal server error
                    15 => true,  // Access denied
                    100 => true, // One of the parameters specified was missing or invalid
                    113 => true, // Invalid user id
                    125 => true, // Invalid group id
                    _ => errorCode > 0 && errorCode != 6 // Все остальные ошибки считаем постоянными, кроме rate limit
                };

                return new PublishResult
                {
                    Success = false,
                    IsPermanentError = isPermanent,
                    ErrorMessage = $"VK API error ({errorCode}): {errorMsg}"
                };
            }

            _logger.LogInformation("Post published successfully to VK group {OwnerId}", ownerId);
            return new PublishResult { Success = true };
        }
        catch (TaskCanceledException)
        {
            return new PublishResult
            {
                Success = false,
                IsPermanentError = false,
                ErrorMessage = "Request timeout or canceled"
            };
        }
        catch (OperationCanceledException)
        {
            return new PublishResult
            {
                Success = false,
                IsPermanentError = false,
                ErrorMessage = "Operation was canceled"
            };
        }
        catch (Exception ex)
        {
            return new PublishResult
            {
                Success = false,
                IsPermanentError = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private string? GetToken(Channel channel, PublicationTarget target)
    {
        // Проверяем CustomParamsJson в target
        if (!string.IsNullOrEmpty(target.CustomParamsJson))
        {
            try
            {
                var customParams = JsonSerializer.Deserialize<JsonElement>(target.CustomParamsJson);
                if (customParams.TryGetProperty("token", out var token))
                    return token.GetString();
            }
            catch { }
        }

        // Проверяем AuthRef в channel
        if (!string.IsNullOrEmpty(channel.AuthRef))
        {
            try
            {
                var authRef = JsonSerializer.Deserialize<JsonElement>(channel.AuthRef);
                if (authRef.TryGetProperty("token", out var token))
                    return token.GetString();
            }
            catch { }
        }

        // Проверяем настройки приложения
        return _configuration["Connectors:VkAccessToken"];
    }

    private async Task<string?> UploadImageAsync(string token, string ownerId, FileStorage image, CancellationToken cancellationToken)
    {
        try
        {
            var groupId = ownerId.TrimStart('-');
            _logger.LogDebug("Getting upload server for VK group {GroupId}", groupId);

            // 1. Получаем upload URL
            var uploadServerUrl = $"https://api.vk.com/method/photos.getWallUploadServer?access_token={token}&group_id={groupId}&v={_apiVersion}";
            var serverResponse = await _httpClient.GetAsync(uploadServerUrl, cancellationToken);
            var serverContent = await serverResponse.Content.ReadAsStringAsync(cancellationToken);
            
            _logger.LogDebug("VK upload server response: {Response}", serverContent);
            var serverResult = JsonSerializer.Deserialize<JsonElement>(serverContent);

            if (serverResult.TryGetProperty("error", out var error))
            {
                var errorMsg = "Unknown error";
                var errorCode = 0;
                
                if (error.TryGetProperty("error_msg", out var msg))
                    errorMsg = msg.GetString() ?? "Unknown error";
                
                if (error.TryGetProperty("error_code", out var code))
                    errorCode = code.GetInt32();

                _logger.LogError("VK API error getting upload server: Code={ErrorCode}, Message={ErrorMessage}", errorCode, errorMsg);
                return null;
            }

            var uploadUrl = serverResult.GetProperty("response").GetProperty("upload_url").GetString();
            if (string.IsNullOrEmpty(uploadUrl))
            {
                _logger.LogError("Upload URL is empty in VK API response");
                return null;
            }

            _logger.LogDebug("Upload URL received, uploading file {FileName}", image.StoredFileName);

            // 2. Загружаем файл
            var fileStream = await _fileStorageService.GetFileStreamAsync(image.Id, cancellationToken);
            if (fileStream == null)
            {
                _logger.LogError("File stream is null for image {ImageId}", image.Id);
                return null;
            }

            using var content = new MultipartFormDataContent();
            using var streamContent = new StreamContent(fileStream);
            streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(image.ContentType);
            content.Add(streamContent, "photo", image.StoredFileName);

            var uploadResponse = await _httpClient.PostAsync(uploadUrl, content, cancellationToken);
            var uploadContent = await uploadResponse.Content.ReadAsStringAsync(cancellationToken);
            
            _logger.LogDebug("VK file upload response: {Response}", uploadContent);
            var uploadResult = JsonSerializer.Deserialize<JsonElement>(uploadContent);

            if (!uploadResult.TryGetProperty("server", out var server) ||
                !uploadResult.TryGetProperty("photo", out var photo) ||
                !uploadResult.TryGetProperty("hash", out var hash))
            {
                _logger.LogError("Invalid upload response format from VK");
                return null;
            }

            // 3. Сохраняем фото
            _logger.LogDebug("Saving photo to VK wall");
            var saveUrl = $"https://api.vk.com/method/photos.saveWallPhoto?access_token={token}&group_id={groupId}&server={server.GetInt32()}&photo={Uri.EscapeDataString(photo.GetString()!)}&hash={Uri.EscapeDataString(hash.GetString()!)}&v={_apiVersion}";
            var saveResponse = await _httpClient.GetAsync(saveUrl, cancellationToken);
            var saveContent = await saveResponse.Content.ReadAsStringAsync(cancellationToken);
            
            _logger.LogDebug("VK save photo response: {Response}", saveContent);
            var saveResult = JsonSerializer.Deserialize<JsonElement>(saveContent);

            if (saveResult.TryGetProperty("error", out var saveError))
            {
                var errorMsg = "Unknown error";
                var errorCode = 0;
                
                if (saveError.TryGetProperty("error_msg", out var msg))
                    errorMsg = msg.GetString() ?? "Unknown error";
                
                if (saveError.TryGetProperty("error_code", out var code))
                    errorCode = code.GetInt32();

                _logger.LogError("VK API error saving photo: Code={ErrorCode}, Message={ErrorMessage}", errorCode, errorMsg);
                return null;
            }

            var photoData = saveResult.GetProperty("response")[0];
            var photoId = photoData.GetProperty("id").GetInt32();
            var photoOwnerId = photoData.GetProperty("owner_id").GetInt32();
            var attachmentId = $"photo{photoOwnerId}_{photoId}";

            _logger.LogDebug("Photo saved successfully, attachment: {AttachmentId}", attachmentId);
            return attachmentId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading image to VK");
            return null;
        }
    }

    private string BuildVkMessage(string title, string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return title;
        }

        var sb = new StringBuilder();
        sb.AppendLine(title);
        sb.AppendLine();
        sb.Append(body);
        
        return sb.ToString().Trim();
    }

    private class FileStorageComparer : IEqualityComparer<FileStorage>
    {
        public bool Equals(FileStorage? x, FileStorage? y)
        {
            if (x == null && y == null) return true;
            if (x == null || y == null) return false;
            return x.Id == y.Id;
        }

        public int GetHashCode(FileStorage obj)
        {
            return obj.Id.GetHashCode();
        }
    }
}

