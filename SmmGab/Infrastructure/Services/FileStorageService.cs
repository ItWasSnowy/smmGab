using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SmmGab.Application.Abstractions;
using SmmGab.Data;
using SmmGab.Domain.Enums;
using SmmGab.Domain.Models;

namespace SmmGab.Infrastructure.Services;

public class FileStorageService : IFileStorageService
{
    private readonly ApplicationDbContext _context;
    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FileStorageService> _logger;
    private readonly string _uploadPath;

    public FileStorageService(
        ApplicationDbContext context,
        IWebHostEnvironment environment,
        IConfiguration configuration,
        ILogger<FileStorageService> logger)
    {
        _context = context;
        _environment = environment;
        _configuration = configuration;
        _logger = logger;
        _uploadPath = Path.Combine(_environment.WebRootPath ?? _environment.ContentRootPath, 
            _configuration["FileStorage:UploadPath"] ?? "wwwroot/Files", "KnowledgeBase");
        
        if (!Directory.Exists(_uploadPath))
            Directory.CreateDirectory(_uploadPath);
        
        _logger.LogInformation("FileStorageService initialized. UploadPath: {UploadPath}, WebRootPath: {WebRootPath}, ContentRootPath: {ContentRootPath}", 
            _uploadPath, _environment.WebRootPath, _environment.ContentRootPath);
    }

    public async Task<FileStorage> SaveFileAsync(Stream fileStream, string fileName, string contentType, long fileSize, CancellationToken cancellationToken)
    {
        var fileId = Guid.NewGuid();
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var storedFileName = $"{fileId}{extension}";
        var filePath = Path.Combine(_uploadPath, storedFileName);
        var relativePath = $"/Files/KnowledgeBase/{storedFileName}";

        // Определяем тип файла
        var fileType = DetermineFileType(contentType, extension);

        // Сохраняем файл на диск
        await using (var file = File.Create(filePath))
        {
            await fileStream.CopyToAsync(file, cancellationToken);
        }

        // Вычисляем хеш
        var hash = await ComputeFileHashAsync(filePath, cancellationToken);

        var fileStorage = new FileStorage
        {
            Id = fileId,
            StoredFileName = storedFileName,
            ContentType = contentType,
            FileSizeBytes = fileSize,
            Type = fileType,
            FilePath = relativePath,
            UploadedAtUtc = DateTime.UtcNow,
            IsTemporary = false,
            Hash = hash
        };

        _context.FileStorage.Add(fileStorage);
        await _context.SaveChangesAsync(cancellationToken);

        return fileStorage;
    }

    public async Task<FileStorage?> GetFileAsync(Guid fileId, CancellationToken cancellationToken)
    {
        return await _context.FileStorage.FindAsync(new object[] { fileId }, cancellationToken);
    }

    public async Task<bool> DeleteFileAsync(Guid fileId, CancellationToken cancellationToken)
    {
        var file = await GetFileAsync(fileId, cancellationToken);
        if (file == null)
            return false;

        var fullPath = Path.Combine(_environment.WebRootPath ?? _environment.ContentRootPath, 
            file.FilePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }

        _context.FileStorage.Remove(file);
        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<Stream?> GetFileStreamAsync(Guid fileId, CancellationToken cancellationToken)
    {
        var file = await GetFileAsync(fileId, cancellationToken);
        if (file == null)
        {
            return null;
        }

        // Используем тот же путь, что и при сохранении
        var fullPath = Path.Combine(_uploadPath, file.StoredFileName);

        if (!File.Exists(fullPath))
        {
            // Пробуем альтернативный путь для обратной совместимости
            var alternativePath = Path.Combine(_environment.WebRootPath ?? _environment.ContentRootPath, 
                file.FilePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            
            if (File.Exists(alternativePath))
            {
                _logger.LogDebug("File found at alternative path: {AlternativePath}", alternativePath);
                return File.OpenRead(alternativePath);
            }
            
            // Логируем для отладки
            _logger.LogError("File not found. FileId: {FileId}, StoredFileName: {StoredFileName}, FilePath: {FilePath}, FullPath: {FullPath}, AlternativePath: {AlternativePath}, UploadPath: {UploadPath}, WebRootPath: {WebRootPath}, ContentRootPath: {ContentRootPath}", 
                fileId, file.StoredFileName, file.FilePath, fullPath, alternativePath, _uploadPath, _environment.WebRootPath, _environment.ContentRootPath);
            
            return null;
        }
        
        _logger.LogDebug("File found at path: {FullPath}", fullPath);

        return File.OpenRead(fullPath);
    }

    public string GetFileUrl(FileStorage file)
    {
        return file.FilePath;
    }

    private FileType DetermineFileType(string contentType, string extension)
    {
        if (contentType.StartsWith("image/") || new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" }.Contains(extension))
            return FileType.Image;
        
        if (contentType.StartsWith("video/") || new[] { ".mp4", ".avi", ".mov", ".mkv" }.Contains(extension))
            return FileType.Video;
        
        if (contentType.StartsWith("audio/") || new[] { ".mp3", ".wav", ".ogg" }.Contains(extension))
            return FileType.Audio;
        
        return FileType.Document;
    }

    private async Task<string> ComputeFileHashAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

