namespace SmmGab.Domain.Models;

public class Project
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid OwnerId { get; set; }
    public Guid? ProjectPhotoFileId { get; set; }
    
    // Navigation properties
    public User Owner { get; set; } = null!;
    public List<Channel> Channels { get; set; } = new();
    public List<Publication> Publications { get; set; } = new();
}

