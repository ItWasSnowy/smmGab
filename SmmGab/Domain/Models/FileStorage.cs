using SmmGab.Domain.Enums;

namespace SmmGab.Domain.Models;

public class FileStorage
{
    public Guid Id { get; set; }
    public string StoredFileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public FileType Type { get; set; }
    public string FilePath { get; set; } = string.Empty;          // Относительный путь: "/Files/KnowledgeBase/guid.ext"
    public string? ThumbnailPath { get; set; }
    public DateTime UploadedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public bool IsTemporary { get; set; }
    public string? Hash { get; set; }
    public Guid? PublicationId { get; set; }
    
    // Navigation properties
    public Publication? Publication { get; set; }
}

