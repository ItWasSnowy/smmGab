using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SmmGab.Application.Abstractions;
using SmmGab.Data;
using SmmGab.Domain.Models;

namespace SmmGab.Infrastructure.Services;

public class DeltaFileExtractor : IDeltaFileExtractor
{
    private readonly ApplicationDbContext _context;

    public DeltaFileExtractor(ApplicationDbContext context)
    {
        _context = context;
    }

    public List<string> ExtractFileUrlsFromDelta(string deltaQuill)
    {
        var urls = new List<string>();
        
        if (string.IsNullOrWhiteSpace(deltaQuill))
            return urls;

        try
        {
            var delta = JObject.Parse(deltaQuill);
            var ops = delta["ops"] as JArray;
            
            if (ops == null)
                return urls;

            foreach (var op in ops)
            {
                if (op["insert"] is JObject insertObj && insertObj["image"] != null)
                {
                    var imageUrl = insertObj["image"]?.ToString();
                    if (!string.IsNullOrEmpty(imageUrl))
                        urls.Add(imageUrl);
                }
                else if (op["insert"] is JObject insertObj2 && insertObj2["video"] != null)
                {
                    var videoUrl = insertObj2["video"]?.ToString();
                    if (!string.IsNullOrEmpty(videoUrl))
                        urls.Add(videoUrl);
                }
            }
        }
        catch
        {
            // Если не удалось распарсить JSON, пробуем регулярное выражение
            var matches = Regex.Matches(deltaQuill, @"/Files/KnowledgeBase/[a-f0-9\-]+\.\w+", RegexOptions.IgnoreCase);
            urls.AddRange(matches.Select(m => m.Value));
        }

        return urls;
    }

    public async Task<List<FileStorage>> GetFilesFromDeltaAsync(string? deltaQuill, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deltaQuill))
            return new List<FileStorage>();

        var urls = ExtractFileUrlsFromDelta(deltaQuill);
        if (urls.Count == 0)
            return new List<FileStorage>();

        var filePaths = urls.Select(url => url.Replace("/Files/KnowledgeBase/", "").Split('.').FirstOrDefault())
            .Where(guid => !string.IsNullOrEmpty(guid) && Guid.TryParse(guid, out _))
            .Select(guid => Guid.Parse(guid!))
            .ToList();

        if (filePaths.Count == 0)
            return new List<FileStorage>();

        var files = await _context.FileStorage
            .Where(f => filePaths.Contains(f.Id))
            .ToListAsync(ct);

        return files;
    }
}

