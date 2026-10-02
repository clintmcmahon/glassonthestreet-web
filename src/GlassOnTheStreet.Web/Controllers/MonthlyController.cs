using System.Security;
using System.Text;
using GlassOnTheStreet.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GlassOnTheStreet.Web.Controllers;

public class MonthlyController(MonthlyReportService reports, IConfiguration configuration) : Controller
{
    [HttpGet("monthly")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken) =>
        View(await reports.ListAsync(cancellationToken));

    [HttpGet("monthly/{year:int}-{month:int}")]
    public async Task<IActionResult> Month(int year, int month, CancellationToken cancellationToken)
    {
        var report = await reports.GetAsync(year, month, cancellationToken);
        return report is null ? NotFound() : View(report);
    }

    [HttpGet("monthly.xml")]
    public async Task<IActionResult> Feed(CancellationToken cancellationToken)
    {
        var baseUrl = (configuration["Site:BaseUrl"] ?? $"{Request.Scheme}://{Request.Host}").TrimEnd('/');
        var months = (await reports.ListAsync(cancellationToken)).Take(12).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<feed xmlns=\"http://www.w3.org/2005/Atom\">");
        sb.AppendLine("  <title>Minneapolis crime, month by month | Glass on the Street</title>");
        sb.AppendLine($"  <link href=\"{baseUrl}/monthly.xml\" rel=\"self\" />");
        sb.AppendLine($"  <link href=\"{baseUrl}/monthly\" />");
        sb.AppendLine($"  <id>{baseUrl}/monthly</id>");
        sb.AppendLine($"  <updated>{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</updated>");

        foreach (var m in months)
        {
            var report = await reports.GetAsync(m.Year, m.Month, cancellationToken);
            if (report is null)
            {
                continue;
            }

            var url = $"{baseUrl}/monthly/{m.Year}-{m.Month:D2}";
            // The month's end is when its figures first existed; the id stays stable across updates.
            var published = new DateTime(m.Year, m.Month, 1).AddMonths(1);
            sb.AppendLine("  <entry>");
            sb.AppendLine($"    <title>{SecurityElement.Escape($"Minneapolis crime in {m.Label}")}</title>");
            sb.AppendLine($"    <link href=\"{url}\" />");
            sb.AppendLine($"    <id>{url}</id>");
            sb.AppendLine($"    <updated>{published:yyyy-MM-ddTHH:mm:ssZ}</updated>");
            sb.AppendLine($"    <summary>{SecurityElement.Escape(report.Narrative[0])}</summary>");
            sb.AppendLine("  </entry>");
        }

        sb.AppendLine("</feed>");
        return Content(sb.ToString(), "application/atom+xml", Encoding.UTF8);
    }
}
