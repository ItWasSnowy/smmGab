using SmmGab.Domain.Enums;

namespace SmmGab.Domain.Models;

public class Channel
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public ChannelType Type { get; set; }
    public string ExternalId { get; set; } = string.Empty;  // owner_id для VK, chat_id для Telegram
    public string? AuthRef { get; set; }    // JSON с токеном: {"token": "..."} для VK, bot token для Telegram
    
    // Navigation properties
    public Project Project { get; set; } = null!;
    public List<PublicationTarget> PublicationTargets { get; set; } = new();
}

