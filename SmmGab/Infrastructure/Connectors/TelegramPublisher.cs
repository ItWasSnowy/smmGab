using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SmmGab.Application.Abstractions;
using SmmGab.Domain.Enums;
using SmmGab.Domain.Models;

namespace SmmGab.Infrastructure.Connectors;

public class TelegramPublisher : IPublisher
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly IDeltaFileExtractor _deltaFileExtractor;
    private readonly IFileStorageService _fileStorageService;
    private readonly ILogger<TelegramPublisher> _logger;

    public TelegramPublisher(
        HttpClient httpClient,
        IConfiguration configuration,
        IDeltaFileExtractor deltaFileExtractor,
        IFileStorageService fileStorageService,
        ILogger<TelegramPublisher> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _deltaFileExtractor = deltaFileExtractor;
        _fileStorageService = fileStorageService;
        _logger = logger;
    }

    public async Task<PublishResult> PublishAsync(
        PublicationTarget target,
        Publication publication,
        Channel channel,
        CancellationToken cancellationToken)
    {
        try
        {
            var botToken = GetBotToken(channel, target);
            if (string.IsNullOrEmpty(botToken))
            {
                return new PublishResult
                {
                    Success = false,
                    IsPermanentError = true,
                    ErrorMessage = "Telegram bot token not found"
                };
            }

            var baseUrl = $"https://api.telegram.org/bot{botToken}/";
            var chatId = channel.ExternalId;

            _logger.LogInformation("Publishing to Telegram channel {ChannelId} (ExternalId: {ExternalId}, BotToken: {BotTokenPrefix}...)", 
                channel.Id, chatId, !string.IsNullOrEmpty(botToken) && botToken.Length > 10 ? botToken.Substring(0, 10) : botToken);

            // Проверяем формат chat_id
            if (string.IsNullOrWhiteSpace(chatId))
            {
                _logger.LogError("Chat ID is empty for channel {ChannelId}", channel.Id);
                return new PublishResult
                {
                    Success = false,
                    IsPermanentError = true,
                    ErrorMessage = "Chat ID is empty"
                };
            }

            // Получаем файлы, связанные с публикацией через PublicationId
            var files = publication.Files?.Where(f => !f.IsTemporary).ToList() ?? new List<FileStorage>();
            
            _logger.LogInformation("Telegram Publisher - Publication files: {FileCount}, Body: {BodyLength}, Files collection: {FilesCollection}", 
                files.Count, 
                string.IsNullOrEmpty(publication.Body) ? "NULL/EMPTY" : publication.Body.Length.ToString(),
                publication.Files == null ? "NULL" : $"{publication.Files.Count} items");
            
            _logger.LogInformation("Telegram Publisher - Found {FileCount} files. File IDs: {FileIds}", 
                files.Count,
                string.Join(", ", files.Select(f => f.Id)));

            // Формируем HTML сообщение для Telegram: заголовок + текст публикации
            var htmlText = BuildTelegramMessage(publication.Text, publication.Body);

            // Если есть файлы
            if (files.Any())
            {
                var images = files.Where(f => f.Type == FileType.Image).ToList();
                var videos = files.Where(f => f.Type == FileType.Video).ToList();
                var documents = files.Where(f => f.Type == FileType.Document).ToList();

                // Если несколько изображений/видео - отправляем медиагруппу
                if ((images.Count + videos.Count) > 1 && (images.Count + videos.Count) <= 10)
                {
                    return await SendMediaGroupAsync(baseUrl, chatId, images, videos, htmlText, cancellationToken);
                }

                // Если один файл или документы
                if (images.Count == 1 && videos.Count == 0 && documents.Count == 0)
                {
                    return await SendPhotoAsync(baseUrl, chatId, images[0], htmlText, cancellationToken);
                }
                else if (videos.Count == 1 && images.Count == 0 && documents.Count == 0)
                {
                    return await SendVideoAsync(baseUrl, chatId, videos[0], htmlText, cancellationToken);
                }
                else if (documents.Count > 0)
                {
                    // Отправляем первый документ с caption, остальные без
                    var result = await SendDocumentAsync(baseUrl, chatId, documents[0], htmlText, cancellationToken);
                    if (!result.Success) return result;

                    // Отправляем остальные документы без caption
                    foreach (var doc in documents.Skip(1))
                    {
                        var docResult = await SendDocumentAsync(baseUrl, chatId, doc, null, cancellationToken);
                        if (!docResult.Success) return docResult;
                    }

                    return result;
                }
            }

            // Если файлов нет - отправляем только текст
            return await SendMessageAsync(baseUrl, chatId, htmlText, cancellationToken);
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

    private string? GetBotToken(Channel channel, PublicationTarget target)
    {
        // Проверяем CustomParamsJson в target
        if (!string.IsNullOrEmpty(target.CustomParamsJson))
        {
            try
            {
                var customParams = JsonSerializer.Deserialize<JsonElement>(target.CustomParamsJson);
                if (customParams.TryGetProperty("botToken", out var token))
                    return token.GetString();
            }
            catch { }
        }

        // Для Telegram AuthRef - это просто строка с токеном (не JSON)
        if (!string.IsNullOrEmpty(channel.AuthRef))
        {
            // Проверяем, не JSON ли это
            if (!channel.AuthRef.TrimStart().StartsWith("{"))
                return channel.AuthRef;
        }

        return null;
    }

    private string BuildTelegramMessage(string title, string? body)
    {
        var sb = new StringBuilder();
        sb.Append($"<b>{EscapeHtml(title)}</b>");
        
        if (!string.IsNullOrWhiteSpace(body))
        {
            sb.Append("\n\n");
            sb.Append(EscapeHtml(body));
        }
        
        return sb.ToString();
    }

    private string EscapeHtml(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }

    private async Task<PublishResult> SendMessageAsync(string baseUrl, string chatId, string text, CancellationToken cancellationToken)
    {
        // Если текст длиннее 4096 символов - разбиваем
        if (text.Length > 4096)
        {
            var parts = SplitTextBySentences(text, 4096);
            var first = true;
            foreach (var part in parts)
            {
                var result = await SendSingleMessageAsync(baseUrl, chatId, part, !first, cancellationToken);
                if (!result.Success) return result;
                first = false;
            }
            return new PublishResult { Success = true };
        }

        return await SendSingleMessageAsync(baseUrl, chatId, text, false, cancellationToken);
    }

    private async Task<PublishResult> SendSingleMessageAsync(string baseUrl, string chatId, string text, bool disableNotification, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{baseUrl}sendMessage";
            var payload = new
            {
                chat_id = chatId,
                text = text,
                parse_mode = "HTML",
                disable_notification = disableNotification
            };

            _logger.LogDebug("Sending message to Telegram: chat_id={ChatId}, text_length={TextLength}", chatId, text.Length);

            var response = await _httpClient.PostAsJsonAsync(url, payload, cancellationToken);
            var result = await response.Content.ReadAsStringAsync(cancellationToken);
            
            _logger.LogDebug("Telegram API response: {Response}", result);

            var json = JsonDocument.Parse(result);

            if (json.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean())
            {
                _logger.LogInformation("Message sent successfully to Telegram chat {ChatId}", chatId);
                return new PublishResult { Success = true };
            }

            var errorMsg = "Unknown error";
            var errorCode = 0;
            
            if (json.RootElement.TryGetProperty("description", out var desc))
                errorMsg = desc.GetString() ?? "Unknown error";
            
            if (json.RootElement.TryGetProperty("error_code", out var code))
                errorCode = code.GetInt32();

            _logger.LogError("Telegram API error: Code={ErrorCode}, Message={ErrorMessage}, ChatId={ChatId}", errorCode, errorMsg, chatId);

            // Определяем, постоянная ли это ошибка
            var isPermanent = errorCode switch
            {
                400 => true, // Bad Request
                401 => true, // Unauthorized
                403 => true, // Forbidden
                404 => true, // Not Found
                429 => false, // Too Many Requests - временная
                _ => true
            };

            return new PublishResult
            {
                Success = false,
                IsPermanentError = isPermanent,
                ErrorMessage = $"Telegram API error ({errorCode}): {errorMsg}"
            };
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse Telegram API response");
            return new PublishResult
            {
                Success = false,
                IsPermanentError = false,
                ErrorMessage = $"Failed to parse Telegram API response: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending message to Telegram");
            return new PublishResult
            {
                Success = false,
                IsPermanentError = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private async Task<PublishResult> SendPhotoAsync(string baseUrl, string chatId, FileStorage image, string? caption, CancellationToken cancellationToken)
    {
        var url = $"{baseUrl}sendPhoto";
        var fileStream = await _fileStorageService.GetFileStreamAsync(image.Id, cancellationToken);
        if (fileStream == null)
            return new PublishResult { Success = false, IsPermanentError = true, ErrorMessage = "File not found" };

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(chatId), "chat_id");
        if (!string.IsNullOrEmpty(caption))
        {
            var captionText = caption.Length > 1024 ? caption.Substring(0, 1021) + "..." : caption;
            content.Add(new StringContent(captionText), "caption");
        }
        content.Add(new StringContent("HTML"), "parse_mode");
        content.Add(new StreamContent(fileStream), "photo", image.StoredFileName);

        try
        {
            _logger.LogDebug("Sending video to Telegram: chat_id={ChatId}", chatId);
            var response = await _httpClient.PostAsync(url, content, cancellationToken);
            var result = await response.Content.ReadAsStringAsync(cancellationToken);
            
            _logger.LogDebug("Telegram API response: {Response}", result);
            var json = JsonDocument.Parse(result);

            if (json.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean())
            {
                _logger.LogInformation("Video sent successfully to Telegram chat {ChatId}", chatId);
                return new PublishResult { Success = true };
            }

            var errorMsg = "Unknown error";
            var errorCode = 0;
            
            if (json.RootElement.TryGetProperty("description", out var desc))
                errorMsg = desc.GetString() ?? "Unknown error";
            
            if (json.RootElement.TryGetProperty("error_code", out var code))
                errorCode = code.GetInt32();

            _logger.LogError("Telegram API error: Code={ErrorCode}, Message={ErrorMessage}, ChatId={ChatId}", errorCode, errorMsg, chatId);

            var isPermanent = errorCode switch
            {
                400 => true,
                401 => true,
                403 => true,
                404 => true,
                429 => false,
                _ => true
            };

            return new PublishResult 
            { 
                Success = false, 
                IsPermanentError = isPermanent, 
                ErrorMessage = $"Telegram API error ({errorCode}): {errorMsg}" 
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending video to Telegram");
            return new PublishResult
            {
                Success = false,
                IsPermanentError = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private async Task<PublishResult> SendVideoAsync(string baseUrl, string chatId, FileStorage video, string? caption, CancellationToken cancellationToken)
    {
        var url = $"{baseUrl}sendVideo";
        var fileStream = await _fileStorageService.GetFileStreamAsync(video.Id, cancellationToken);
        if (fileStream == null)
            return new PublishResult { Success = false, IsPermanentError = true, ErrorMessage = "File not found" };

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(chatId), "chat_id");
        if (!string.IsNullOrEmpty(caption))
        {
            var captionText = caption.Length > 1024 ? caption.Substring(0, 1021) + "..." : caption;
            content.Add(new StringContent(captionText), "caption");
        }
        content.Add(new StringContent("HTML"), "parse_mode");
        content.Add(new StreamContent(fileStream), "video", video.StoredFileName);

        try
        {
            _logger.LogDebug("Sending document to Telegram: chat_id={ChatId}", chatId);
            var response = await _httpClient.PostAsync(url, content, cancellationToken);
            var result = await response.Content.ReadAsStringAsync(cancellationToken);
            
            _logger.LogDebug("Telegram API response: {Response}", result);
            var json = JsonDocument.Parse(result);

            if (json.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean())
            {
                _logger.LogInformation("Document sent successfully to Telegram chat {ChatId}", chatId);
                return new PublishResult { Success = true };
            }

            var errorMsg = "Unknown error";
            var errorCode = 0;
            
            if (json.RootElement.TryGetProperty("description", out var desc))
                errorMsg = desc.GetString() ?? "Unknown error";
            
            if (json.RootElement.TryGetProperty("error_code", out var code))
                errorCode = code.GetInt32();

            _logger.LogError("Telegram API error: Code={ErrorCode}, Message={ErrorMessage}, ChatId={ChatId}", errorCode, errorMsg, chatId);

            var isPermanent = errorCode switch
            {
                400 => true,
                401 => true,
                403 => true,
                404 => true,
                429 => false,
                _ => true
            };

            return new PublishResult 
            { 
                Success = false, 
                IsPermanentError = isPermanent, 
                ErrorMessage = $"Telegram API error ({errorCode}): {errorMsg}" 
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending document to Telegram");
            return new PublishResult
            {
                Success = false,
                IsPermanentError = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private async Task<PublishResult> SendDocumentAsync(string baseUrl, string chatId, FileStorage document, string? caption, CancellationToken cancellationToken)
    {
        var url = $"{baseUrl}sendDocument";
        var fileStream = await _fileStorageService.GetFileStreamAsync(document.Id, cancellationToken);
        if (fileStream == null)
            return new PublishResult { Success = false, IsPermanentError = true, ErrorMessage = "File not found" };

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(chatId), "chat_id");
        if (!string.IsNullOrEmpty(caption))
        {
            var captionText = caption.Length > 1024 ? caption.Substring(0, 1021) + "..." : caption;
            content.Add(new StringContent(captionText), "caption");
        }
        content.Add(new StringContent("HTML"), "parse_mode");
        content.Add(new StreamContent(fileStream), "document", document.StoredFileName);

        try
        {
            _logger.LogDebug("Sending photo to Telegram: chat_id={ChatId}", chatId);
            var response = await _httpClient.PostAsync(url, content, cancellationToken);
            var result = await response.Content.ReadAsStringAsync(cancellationToken);
            
            _logger.LogDebug("Telegram API response: {Response}", result);
            var json = JsonDocument.Parse(result);

            if (json.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean())
            {
                _logger.LogInformation("Photo sent successfully to Telegram chat {ChatId}", chatId);
                return new PublishResult { Success = true };
            }

            var errorMsg = "Unknown error";
            var errorCode = 0;
            
            if (json.RootElement.TryGetProperty("description", out var desc))
                errorMsg = desc.GetString() ?? "Unknown error";
            
            if (json.RootElement.TryGetProperty("error_code", out var code))
                errorCode = code.GetInt32();

            _logger.LogError("Telegram API error: Code={ErrorCode}, Message={ErrorMessage}, ChatId={ChatId}", errorCode, errorMsg, chatId);

            var isPermanent = errorCode switch
            {
                400 => true,
                401 => true,
                403 => true,
                404 => true,
                429 => false,
                _ => true
            };

            return new PublishResult 
            { 
                Success = false, 
                IsPermanentError = isPermanent, 
                ErrorMessage = $"Telegram API error ({errorCode}): {errorMsg}" 
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending photo to Telegram");
            return new PublishResult
            {
                Success = false,
                IsPermanentError = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private async Task<PublishResult> SendMediaGroupAsync(string baseUrl, string chatId, List<FileStorage> images, List<FileStorage> videos, string? caption, CancellationToken cancellationToken)
    {
        var url = $"{baseUrl}sendMediaGroup";
        var media = new List<object>();
        var allFiles = images.Select(i => new { Type = "photo", File = i })
            .Concat(videos.Select(v => new { Type = "video", File = v }))
            .ToList();

        var captionText = caption;
        if (!string.IsNullOrEmpty(caption) && caption.Length > 1024)
        {
            // Если caption слишком длинный, отправляем файлы без caption, затем текст отдельно
            captionText = null;
        }

        foreach (var item in allFiles.Take(10))
        {
            var fileId = $"attach://{item.File.Id}";
            media.Add(new
            {
                type = item.Type,
                media = fileId,
                caption = media.Count == 0 ? captionText : null,
                parse_mode = "HTML"
            });
        }

        try
        {
            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(chatId), "chat_id");
            content.Add(new StringContent(JsonSerializer.Serialize(media)), "media");

            foreach (var item in allFiles.Take(10))
            {
                var fileStream = await _fileStorageService.GetFileStreamAsync(item.File.Id, cancellationToken);
                if (fileStream != null)
                {
                    content.Add(new StreamContent(fileStream), item.File.Id.ToString(), item.File.StoredFileName);
                }
            }

            _logger.LogDebug("Sending media group to Telegram: chat_id={ChatId}, files_count={Count}", chatId, allFiles.Count);
            var response = await _httpClient.PostAsync(url, content, cancellationToken);
            var result = await response.Content.ReadAsStringAsync(cancellationToken);
            
            _logger.LogDebug("Telegram API response: {Response}", result);
            var json = JsonDocument.Parse(result);

            if (json.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean())
            {
                _logger.LogInformation("Media group sent successfully to Telegram chat {ChatId}", chatId);
                // Если был длинный caption, отправляем его отдельно
                if (caption != null && caption.Length > 1024)
                {
                    return await SendMessageAsync(baseUrl, chatId, caption, cancellationToken);
                }
                return new PublishResult { Success = true };
            }

            var errorMsg = "Unknown error";
            var errorCode = 0;
            
            if (json.RootElement.TryGetProperty("description", out var desc))
                errorMsg = desc.GetString() ?? "Unknown error";
            
            if (json.RootElement.TryGetProperty("error_code", out var code))
                errorCode = code.GetInt32();

            _logger.LogError("Telegram API error: Code={ErrorCode}, Message={ErrorMessage}, ChatId={ChatId}", errorCode, errorMsg, chatId);

            var isPermanent = errorCode switch
            {
                400 => true,
                401 => true,
                403 => true,
                404 => true,
                429 => false,
                _ => true
            };

            return new PublishResult 
            { 
                Success = false, 
                IsPermanentError = isPermanent, 
                ErrorMessage = $"Telegram API error ({errorCode}): {errorMsg}" 
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending media group to Telegram");
            return new PublishResult
            {
                Success = false,
                IsPermanentError = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private List<string> SplitTextBySentences(string text, int maxLength)
    {
        var parts = new List<string>();
        var sentences = Regex.Split(text, @"(?<=[.!?])\s+");
        var currentPart = new StringBuilder();

        foreach (var sentence in sentences)
        {
            if (currentPart.Length + sentence.Length + 1 > maxLength)
            {
                if (currentPart.Length > 0)
                {
                    parts.Add(currentPart.ToString());
                    currentPart.Clear();
                }
                // Если одно предложение длиннее maxLength, разбиваем по словам
                if (sentence.Length > maxLength)
                {
                    var words = sentence.Split(' ');
                    foreach (var word in words)
                    {
                        if (currentPart.Length + word.Length + 1 > maxLength)
                        {
                            if (currentPart.Length > 0)
                            {
                                parts.Add(currentPart.ToString());
                                currentPart.Clear();
                            }
                        }
                        if (currentPart.Length > 0) currentPart.Append(' ');
                        currentPart.Append(word);
                    }
                }
                else
                {
                    currentPart.Append(sentence);
                }
            }
            else
            {
                if (currentPart.Length > 0) currentPart.Append(' ');
                currentPart.Append(sentence);
            }
        }

        if (currentPart.Length > 0)
            parts.Add(currentPart.ToString());

        return parts;
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

