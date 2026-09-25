using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace GlassOnTheStreet.Web.Controllers;

/// <summary>
/// Served from a controller rather than a static wwwroot/sitemap.xml so
/// lastmod for the map and home pages (whose content -- the report count
/// -- changes essentially continuously) reflects the actual current time
/// instead of going stale the moment someone forgets to hand-edit a file.
/// </summary>
public class SitemapController(IConfiguration configuration) : Controller
{
    private record SitemapEntry(string Path, string ChangeFreq, DateTime? LastModUtc);

    [HttpGet("sitemap.xml")]
    public IActionResult Index()
    {
        var baseUrl = (configuration["Site:BaseUrl"] ?? $"{Request.Scheme}://{Request.Host}").TrimEnd('/');
        var now = DateTime.UtcNow;

        var entries = new[]
        {
            new SitemapEntry("/", "daily", now),
            new SitemapEntry("/map", "daily", now),
            new SitemapEntry("/report", "monthly", null),
            new SitemapEntry("/privacy", "monthly", null)
        };

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">");
        foreach (var entry in entries)
        {
            sb.AppendLine("  <url>");
            sb.AppendLine($"    <loc>{baseUrl}{entry.Path}</loc>");
            if (entry.LastModUtc is not null)
            {
                sb.AppendLine($"    <lastmod>{entry.LastModUtc:yyyy-MM-dd}</lastmod>");
            }
            sb.AppendLine($"    <changefreq>{entry.ChangeFreq}</changefreq>");
            sb.AppendLine("  </url>");
        }
        sb.AppendLine("</urlset>");

        return Content(sb.ToString(), "application/xml", Encoding.UTF8);
    }
}
