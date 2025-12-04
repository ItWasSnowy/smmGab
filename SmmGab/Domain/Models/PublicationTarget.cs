using SmmGab.Domain.Enums;

namespace SmmGab.Domain.Models;

public class PublicationTarget
{
    public Guid Id { get; set; }
    public Guid PublicationId { get; set; }
    public Guid ChannelId { get; set; }
    public ChannelType ChannelType { get; set; }
    public string? CustomParamsJson { get; set; }
    public TargetStatus Status { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
    
    // Navigation properties
    public Publication Publication { get; set; } = null!;
    public Channel Channel { get; set; } = null!;
}

