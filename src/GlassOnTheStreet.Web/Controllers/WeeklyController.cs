using System.Globalization;
using System.Security;
using System.Text;
using GlassOnTheStreet.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GlassOnTheStreet.Web.Controllers;

public class WeeklyController(WeeklyReportService reports, IConfiguration configuration) : Controller
{
    [HttpGet("weekly")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken) =>
        View(await reports.ListAsync(cancellationToken: cancellationToken));

    [HttpGet("weekly/{date}")]
    public async Task<IActionResult> Week(string date, CancellationToken cancellationToken)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
        {
            return NotFound();
        }

        var report = await reports.GetAsync(start, cancellationToken);
        return report is null ? NotFound() : View(report);
    }

    [HttpGet("weekly.xml")]
    public async Task<IActionResult> Feed(CancellationToken cancellationToken)
    {
        var baseUrl = (configuration["Site:BaseUrl"] ?? $"{Request.Scheme}://{Request.Host}").TrimEnd('/');
        var weeks = (await reports.ListAsync(12, cancellationToken)).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<feed xmlns=\"http://www.w3.org/2005/Atom\">");
        sb.AppendLine("  <title>Minneapolis crime, week by week | Glass on the Street</title>");
        sb.AppendLine($"  <link href=\"{baseUrl}/weekly.xml\" rel=\"self\" />");
        sb.AppendLine($"  <link href=\"{baseUrl}/weekly\" />");
        sb.AppendLine($"  <id>{baseUrl}/weekly</id>");
        sb.AppendLine($"  <updated>{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</updated>");

        foreach (var w in weeks)
        {
            var report = await reports.GetAsync(w.Start, cancellationToken);
            if (report is null)
            {
                continue;
            }

            var url = $"{baseUrl}/weekly/{w.Start:yyyy-MM-dd}";
            // The week's end is when its figures first existed; the id stays stable across updates.
            var published = w.Start.AddDays(7).ToDateTime(TimeOnly.MinValue);
            sb.AppendLine("  <entry>");
            sb.AppendLine($"    <title>{SecurityElement.Escape($"Minneapolis crime, week of {w.Label}")}</title>");
            sb.AppendLine($"    <link href=\"{url}\" />");
            sb.AppendLine($"    <id>{url}</id>");
            sb.AppendLine($"    <updated>{published:yyyy-MM-ddTHH:mm:ssZ}</updated>");
            sb.AppendLine($"    <summary>{SecurityElement.Escape(string.Join(" ", report.Narrative.Take(2)))}</summary>");
            sb.AppendLine("  </entry>");
        }

        sb.AppendLine("</feed>");
        return Content(sb.ToString(), "application/atom+xml", Encoding.UTF8);
    }
}
