using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmmGab.Data;
using SmmGab.Models;

namespace SmmGab.Controllers;

[Authorize]
public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly ApplicationDbContext _context;

    public HomeController(ILogger<HomeController> logger, ApplicationDbContext context)
    {
        _logger = logger;
        _context = context;
    }

    public async Task<IActionResult> Index()
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        
        // Проверяем, есть ли выбранный проект в сессии
        var selectedProjectId = HttpContext.Session.GetString("SelectedProjectId");
        
        if (string.IsNullOrEmpty(selectedProjectId))
        {
            // Если проекта нет, показываем страницу выбора проекта
            var projects = await _context.Projects
                .Where(p => p.OwnerId == userId)
                .OrderByDescending(p => p.CreatedAtUtc)
                .ToListAsync();
            
            return View("SelectProject", projects);
        }
        
        // Если проект выбран, показываем дашборд проекта
        if (Guid.TryParse(selectedProjectId, out var projectId))
        {
            return RedirectToAction("Dashboard", new { id = projectId });
        }
        
        return View("SelectProject", new List<Domain.Models.Project>());
    }

    [HttpPost]
    public IActionResult SelectProject(Guid projectId)
    {
        HttpContext.Session.SetString("SelectedProjectId", projectId.ToString());
        return RedirectToAction("Dashboard", new { id = projectId });
    }

    public async Task<IActionResult> Dashboard(Guid id)
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        
        var project = await _context.Projects
            .Include(p => p.Channels)
            .Include(p => p.Publications)
            .ThenInclude(p => p.Targets)
            .ThenInclude(t => t.Channel)
            .FirstOrDefaultAsync(p => p.Id == id && p.OwnerId == userId);

        if (project == null)
        {
            HttpContext.Session.Remove("SelectedProjectId");
            return RedirectToAction("Index");
        }

        // Сохраняем выбранный проект в сессии
        HttpContext.Session.SetString("SelectedProjectId", project.Id.ToString());

        ViewBag.Project = project;
        
        // Получаем последние публикации
        var recentPublications = project.Publications
            .OrderByDescending(p => p.CreatedAtUtc)
            .Take(10)
            .ToList();

        ViewBag.RecentPublications = recentPublications;

        return View(project);
    }

    [HttpPost]
    public IActionResult ChangeProject()
    {
        HttpContext.Session.Remove("SelectedProjectId");
        return RedirectToAction("Index");
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = System.Diagnostics.Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}

