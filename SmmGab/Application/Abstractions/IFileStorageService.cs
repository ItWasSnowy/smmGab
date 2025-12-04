using SmmGab.Domain.Models;

namespace SmmGab.Application.Abstractions;

public interface IFileStorageService
{
    Task<FileStorage> SaveFileAsync(Stream fileStream, string fileName, string contentType, long fileSize, CancellationToken cancellationToken);
    Task<FileStorage?> GetFileAsync(Guid fileId, CancellationToken cancellationToken);
    Task<bool> DeleteFileAsync(Guid fileId, CancellationToken cancellationToken);
    Task<Stream?> GetFileStreamAsync(Guid fileId, CancellationToken cancellationToken);
    string GetFileUrl(FileStorage file);
}

