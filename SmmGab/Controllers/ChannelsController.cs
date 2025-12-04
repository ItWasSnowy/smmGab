using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using SmmGab.Data;
using SmmGab.Domain.Enums;
using SmmGab.Domain.Models;

namespace SmmGab.Controllers;

[Authorize]
public class ChannelsController : Controller
{
    private readonly ApplicationDbContext _context;

    public ChannelsController(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<IActionResult> Index([FromQuery] Guid? projectId)
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        
        // Если projectId не передан, берем из сессии
        if (!projectId.HasValue)
        {
            var sessionProjectId = HttpContext.Session.GetString("SelectedProjectId");
            if (!string.IsNullOrEmpty(sessionProjectId) && Guid.TryParse(sessionProjectId, out var sessionId))
            {
                projectId = sessionId;
            }
        }
        
        var query = _context.Channels
            .Include(c => c.Project)
            .Where(c => c.Project.OwnerId == userId);

        if (projectId.HasValue)
            query = query.Where(c => c.ProjectId == projectId.Value);

        var channels = await query
            .OrderBy(c => c.DisplayName)
            .ToListAsync();

        ViewBag.Projects = await _context.Projects
            .Where(p => p.OwnerId == userId)
            .OrderBy(p => p.Name)
            .ToListAsync();

        return View(channels);
    }

    [HttpGet]
    public async Task<IActionResult> Create([FromQuery] Guid? projectId)
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        
        ViewBag.Projects = new SelectList(
            await _context.Projects
                .Where(p => p.OwnerId == userId)
                .OrderBy(p => p.Name)
                .ToListAsync(),
            "Id",
            "Name",
            projectId);

        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateChannelViewModel model)
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);

        if (!ModelState.IsValid)
        {
            ViewBag.Projects = new SelectList(
                await _context.Projects
                    .Where(p => p.OwnerId == userId)
                    .OrderBy(p => p.Name)
                    .ToListAsync(),
                "Id",
                "Name",
                model.ProjectId);

            return View(model);
        }

        var project = await _context.Projects
            .FirstOrDefaultAsync(p => p.Id == model.ProjectId && p.OwnerId == userId);

        if (project == null)
        {
            ModelState.AddModelError("", "Проект не найден");
            ViewBag.Projects = new SelectList(
                await _context.Projects
                    .Where(p => p.OwnerId == userId)
                    .OrderBy(p => p.Name)
                    .ToListAsync(),
                "Id",
                "Name",
                model.ProjectId);

            return View(model);
        }

        var channel = new Channel
        {
            Id = Guid.NewGuid(),
            ProjectId = model.ProjectId,
            DisplayName = model.DisplayName,
            Type = model.Type,
            ExternalId = model.ExternalId,
            AuthRef = model.AuthRef
        };

        _context.Channels.Add(channel);
        await _context.SaveChangesAsync();

        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Details(Guid id)
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        var channel = await _context.Channels
            .Include(c => c.Project)
            .Include(c => c.PublicationTargets)
            .ThenInclude(pt => pt.Publication)
            .FirstOrDefaultAsync(c => c.Id == id && c.Project.OwnerId == userId);

        if (channel == null)
            return NotFound();

        return View(channel);
    }

    [HttpGet]
    public async Task<IActionResult> Edit(Guid id)
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        var channel = await _context.Channels
            .Include(c => c.Project)
            .FirstOrDefaultAsync(c => c.Id == id && c.Project.OwnerId == userId);

        if (channel == null)
            return NotFound();

        var model = new EditChannelViewModel
        {
            Id = channel.Id,
            ProjectId = channel.ProjectId,
            DisplayName = channel.DisplayName,
            Type = channel.Type,
            ExternalId = channel.ExternalId,
            AuthRef = channel.AuthRef
        };

        ViewBag.Projects = new SelectList(
            await _context.Projects
                .Where(p => p.OwnerId == userId)
                .OrderBy(p => p.Name)
                .ToListAsync(),
            "Id",
            "Name",
            channel.ProjectId);

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(EditChannelViewModel model)
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);

        if (!ModelState.IsValid)
        {
            ViewBag.Projects = new SelectList(
                await _context.Projects
                    .Where(p => p.OwnerId == userId)
                    .OrderBy(p => p.Name)
                    .ToListAsync(),
                "Id",
                "Name",
                model.ProjectId);

            return View(model);
        }

        var channel = await _context.Channels
            .Include(c => c.Project)
            .FirstOrDefaultAsync(c => c.Id == model.Id && c.Project.OwnerId == userId);

        if (channel == null)
            return NotFound();

        channel.DisplayName = model.DisplayName;
        channel.ExternalId = model.ExternalId;
        channel.AuthRef = model.AuthRef;
        channel.Type = model.Type;

        if (channel.ProjectId != model.ProjectId)
        {
            var newProject = await _context.Projects
                .FirstOrDefaultAsync(p => p.Id == model.ProjectId && p.OwnerId == userId);

            if (newProject == null)
            {
                ModelState.AddModelError("", "Проект не найден");
                ViewBag.Projects = new SelectList(
                    await _context.Projects
                        .Where(p => p.OwnerId == userId)
                        .OrderBy(p => p.Name)
                        .ToListAsync(),
                    "Id",
                    "Name",
                    model.ProjectId);

                return View(model);
            }

            channel.ProjectId = model.ProjectId;
        }

        await _context.SaveChangesAsync();

        return RedirectToAction(nameof(Details), new { id = channel.Id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        var channel = await _context.Channels
            .Include(c => c.Project)
            .FirstOrDefaultAsync(c => c.Id == id && c.Project.OwnerId == userId);

        if (channel == null)
            return NotFound();

        _context.Channels.Remove(channel);
        await _context.SaveChangesAsync();

        return RedirectToAction(nameof(Index));
    }
}

public class CreateChannelViewModel
{
    [Required(ErrorMessage = "Выберите проект")]
    [Display(Name = "Проект")]
    public Guid ProjectId { get; set; }

    [Required(ErrorMessage = "Введите название канала")]
    [StringLength(200, ErrorMessage = "Название должно быть не более 200 символов")]
    [Display(Name = "Название канала")]
    public string DisplayName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Выберите тип канала")]
    [Display(Name = "Тип канала")]
    public ChannelType Type { get; set; }

    [Required(ErrorMessage = "Введите внешний ID")]
    [Display(Name = "Внешний ID")]
    public string ExternalId { get; set; } = string.Empty;

    [Display(Name = "Токен авторизации")]
    public string? AuthRef { get; set; }
}

public class EditChannelViewModel
{
    public Guid Id { get; set; }

    [Required(ErrorMessage = "Выберите проект")]
    [Display(Name = "Проект")]
    public Guid ProjectId { get; set; }

    [Required(ErrorMessage = "Введите название канала")]
    [StringLength(200, ErrorMessage = "Название должно быть не более 200 символов")]
    [Display(Name = "Название канала")]
    public string DisplayName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Выберите тип канала")]
    [Display(Name = "Тип канала")]
    public ChannelType Type { get; set; }

    [Required(ErrorMessage = "Введите внешний ID")]
    [Display(Name = "Внешний ID")]
    public string ExternalId { get; set; } = string.Empty;

    [Display(Name = "Токен авторизации")]
    public string? AuthRef { get; set; }
}

