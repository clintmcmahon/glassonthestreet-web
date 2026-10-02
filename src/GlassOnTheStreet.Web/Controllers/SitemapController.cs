using System.Text;
using GlassOnTheStreet.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GlassOnTheStreet.Web.Controllers;

/// <summary>
/// Served from a controller rather than a static wwwroot/sitemap.xml so
/// lastmod for the map and home pages (whose content -- the report count
/// -- changes essentially continuously) reflects the actual current time
/// instead of going stale the moment someone forgets to hand-edit a file.
/// </summary>
public class SitemapController(IConfiguration configuration, ITrendsService trendsService, MonthlyReportService monthlyReports) : Controller
{
    private record SitemapEntry(string Path, string ChangeFreq, DateTime? LastModUtc);

    [HttpGet("sitemap.xml")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var baseUrl = (configuration["Site:BaseUrl"] ?? $"{Request.Scheme}://{Request.Host}").TrimEnd('/');
        var now = DateTime.UtcNow;

        var areas = await trendsService.GetAreaIndexAsync(cancellationToken);
        var months = await monthlyReports.ListAsync(cancellationToken);

        var entries = new List<SitemapEntry>
        {
            new SitemapEntry("/", "daily", now),
            new SitemapEntry("/map", "daily", now),
            new SitemapEntry("/trends", "daily", now),
            new SitemapEntry("/report", "monthly", null),
            new SitemapEntry("/privacy", "monthly", null),
            new SitemapEntry("/neighborhoods", "daily", now),
            new SitemapEntry("/crime", "daily", now),
            new SitemapEntry("/near", "monthly", null),
            new SitemapEntry("/compare", "monthly", null),
            new SitemapEntry("/monthly", "monthly", now),
            new SitemapEntry("/methodology", "monthly", null),
            new SitemapEntry("/data", "weekly", now)
        };

        // One permanent page per finished month. Older months rarely change.
        foreach (var month in months)
        {
            entries.Add(new SitemapEntry($"/monthly/{month.Year}-{month.Month:D2}", "monthly", null));
        }

        // Every ward and neighborhood page with enough data to be worth indexing
        // (the page itself marks thinner ones noindex).
        foreach (var area in areas.Wards.Concat(areas.Neighborhoods).Where(a => a.Counts.Sum() >= 30))
        {
            entries.Add(new SitemapEntry(area.Url, "daily", now));
        }

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
