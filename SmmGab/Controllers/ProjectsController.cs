using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmmGab.Data;
using SmmGab.Domain.Models;

namespace SmmGab.Controllers;

[Authorize]
public class ProjectsController : Controller
{
    private readonly ApplicationDbContext _context;

    public ProjectsController(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<IActionResult> Index()
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        var projects = await _context.Projects
            .Where(p => p.OwnerId == userId)
            .OrderByDescending(p => p.CreatedAtUtc)
            .ToListAsync();

        return View(projects);
    }

    [HttpGet]
    public IActionResult Create()
    {
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateProjectViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = model.Name,
            OwnerId = userId,
            CreatedAtUtc = DateTime.UtcNow
        };

        _context.Projects.Add(project);
        await _context.SaveChangesAsync();

        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Details(Guid id)
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        var project = await _context.Projects
            .Include(p => p.Channels)
            .Include(p => p.Publications)
            .FirstOrDefaultAsync(p => p.Id == id && p.OwnerId == userId);

        if (project == null)
            return NotFound();

        return View(project);
    }
}

public class CreateProjectViewModel
{
    [Required(ErrorMessage = "Название проекта обязательно")]
    [StringLength(100, ErrorMessage = "Название должно быть не более 100 символов")]
    public string Name { get; set; } = string.Empty;
}

