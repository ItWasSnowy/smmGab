using SmmGab.Domain.Models;

namespace SmmGab.Application.Abstractions;

public interface IDeltaFileExtractor
{
    Task<List<FileStorage>> GetFilesFromDeltaAsync(string? deltaQuill, CancellationToken ct);
    List<string> ExtractFileUrlsFromDelta(string deltaQuill);
}

