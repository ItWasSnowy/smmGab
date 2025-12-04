using SmmGab.Domain.Enums;

namespace SmmGab.Domain.Models;

public class Publication
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Text { get; set; } = string.Empty;              // Заголовок/название публикации
    public string? Body { get; set; }                             // Текст публикации (простой текст)
    public string? DeltaQuill { get; set; }        // Форматированный контент в формате Delta Quill (для обратной совместимости)
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ScheduledAtUtc { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
    public int? ClientTimezoneMinutes { get; set; }
    public bool IsPublish { get; set; }           // true = публикация, false = черновик
    public bool IsNow { get; set; }                 // Немедленная публикация
    public bool IsLater { get; set; }               // Запланированная публикация
    public PublicationStatus Status { get; set; }
    public Guid AuthorId { get; set; }
    
    // Navigation properties
    public Project Project { get; set; } = null!;
    public User Author { get; set; } = null!;
    public List<PublicationTarget> Targets { get; set; } = new();
    public List<FileStorage> Files { get; set; } = new();
}

