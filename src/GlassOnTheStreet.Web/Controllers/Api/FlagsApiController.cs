using System.ComponentModel.DataAnnotations;
using GlassOnTheStreet.Web.Data;
using GlassOnTheStreet.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace GlassOnTheStreet.Web.Controllers.Api;

public class FlagReportRequest
{
    [MaxLength(500)]
    public string? Reason { get; set; }
}

[ApiController]
[Route("api/reports/{id:int}/flag")]
[EnableRateLimiting("flag-report")]
public class FlagsApiController(GlassOnTheStreetContext db) : ControllerBase
{
    /// <summary>
    /// Flags a report needs before it leaves the public map. One anonymous request can't hide
    /// anyone's report; every flag is still kept for the admin queue.
    /// </summary>
    public const int HideThreshold = 3;

    [HttpPost]
    public async Task<IActionResult> Flag(int id, [FromBody] FlagReportRequest? request, CancellationToken cancellationToken)
    {
        // Only resident reports can be flagged; imported MPD rows aren't shown as individual reports.
        var report = await db.Reports.FirstOrDefaultAsync(
            r => r.Id == id && r.SourceType == SourceType.UserReport, cancellationToken);
        if (report is null || report.Status == ReportStatus.Removed)
        {
            return NotFound();
        }

        var reason = request?.Reason?.Trim();
        db.ReportFlags.Add(new ReportFlag { ReportId = id, Reason = string.IsNullOrEmpty(reason) ? null : reason });
        await db.SaveChangesAsync(cancellationToken);

        if (report.Status == ReportStatus.Active
            && await db.ReportFlags.CountAsync(f => f.ReportId == id, cancellationToken) >= HideThreshold)
        {
            report.Status = ReportStatus.Flagged;
            await db.SaveChangesAsync(cancellationToken);
        }

        return Ok(new { flagged = true });
    }
}
