using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmmGab.Data;
using SmmGab.Domain.Models;

namespace SmmGab.Controllers.Api;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ChannelsController : ControllerBase
{
    private readonly ApplicationDbContext _context;

    public ChannelsController(ApplicationDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Channel>>> GetChannels([FromQuery] Guid? projectId)
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        var query = _context.Channels
            .Include(c => c.Project)
            .Where(c => c.Project.OwnerId == userId);

        if (projectId.HasValue)
            query = query.Where(c => c.ProjectId == projectId.Value);

        var channels = await query.ToListAsync();
        return Ok(channels);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Channel>> GetChannel(Guid id)
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        var channel = await _context.Channels
            .Include(c => c.Project)
            .FirstOrDefaultAsync(c => c.Id == id && c.Project.OwnerId == userId);

        if (channel == null)
            return NotFound();

        return Ok(channel);
    }

    [HttpPost]
    public async Task<ActionResult<Channel>> CreateChannel([FromBody] CreateChannelDto dto)
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        
        var project = await _context.Projects
            .FirstOrDefaultAsync(p => p.Id == dto.ProjectId && p.OwnerId == userId);

        if (project == null)
            return BadRequest("Project not found");

        var channel = new Channel
        {
            Id = Guid.NewGuid(),
            ProjectId = dto.ProjectId,
            DisplayName = dto.DisplayName,
            Type = dto.Type,
            ExternalId = dto.ExternalId,
            AuthRef = dto.AuthRef
        };

        _context.Channels.Add(channel);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetChannel), new { id = channel.Id }, channel);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateChannel(Guid id, [FromBody] UpdateChannelDto dto)
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        var channel = await _context.Channels
            .Include(c => c.Project)
            .FirstOrDefaultAsync(c => c.Id == id && c.Project.OwnerId == userId);

        if (channel == null)
            return NotFound();

        channel.DisplayName = dto.DisplayName;
        channel.ExternalId = dto.ExternalId;
        channel.AuthRef = dto.AuthRef;

        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteChannel(Guid id)
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        var channel = await _context.Channels
            .Include(c => c.Project)
            .FirstOrDefaultAsync(c => c.Id == id && c.Project.OwnerId == userId);

        if (channel == null)
            return NotFound();

        _context.Channels.Remove(channel);
        await _context.SaveChangesAsync();
        return NoContent();
    }
}

public class CreateChannelDto
{
    public Guid ProjectId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public Domain.Enums.ChannelType Type { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string? AuthRef { get; set; }
}

public class UpdateChannelDto
{
    public string DisplayName { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public string? AuthRef { get; set; }
}

