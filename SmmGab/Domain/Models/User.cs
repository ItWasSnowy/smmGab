using Microsoft.AspNetCore.Identity;

namespace SmmGab.Domain.Models;

public class User : IdentityUser<Guid>
{
    public string Name { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }
    public bool IsActive { get; set; } = true;
    
    // Navigation properties
    public List<Project> Projects { get; set; } = new();
    public List<Publication> Publications { get; set; } = new();
}

