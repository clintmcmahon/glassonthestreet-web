using GlassOnTheStreet.Web.Data;
using GlassOnTheStreet.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GlassOnTheStreet.Web.Controllers.Api;

public class FlagReportRequest
{
    public string? Reason { get; set; }
}

[ApiController]
[Route("api/reports/{id:int}/flag")]
public class FlagsApiController(GlassOnTheStreetContext db) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Flag(int id, [FromBody] FlagReportRequest? request, CancellationToken cancellationToken)
    {
        var report = await db.Reports.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (report is null || report.Status == ReportStatus.Removed)
        {
            return NotFound();
        }

        db.ReportFlags.Add(new ReportFlag { ReportId = id, Reason = request?.Reason });

        if (report.Status == ReportStatus.Active)
        {
            report.Status = ReportStatus.Flagged;
        }

        await db.SaveChangesAsync(cancellationToken);

        return Ok(new { flagged = true });
    }
}
