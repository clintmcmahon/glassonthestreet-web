using GlassOnTheStreet.Web.Data;
using GlassOnTheStreet.Web.Infrastructure;
using GlassOnTheStreet.Web.Models;
using GlassOnTheStreet.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GlassOnTheStreet.Web.Controllers.Admin;

[Route("admin")]
[AdminBasicAuth]
public class AdminController(GlassOnTheStreetContext db, IOfficialDataImportService importService) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var reports = await db.Reports
            .Include(r => r.Flags)
            .OrderByDescending(r => r.CreatedAt)
            .Take(200)
            .ToListAsync(cancellationToken);

        return View(reports);
    }

    [HttpPost("import")]
    [Microsoft.AspNetCore.Mvc.ValidateAntiForgeryToken]
    public async Task<IActionResult> Import([FromForm] int lookbackDays, CancellationToken cancellationToken)
    {
        var days = lookbackDays <= 0 ? 180 : lookbackDays;
        var result = await importService.ImportAsync(days, cancellationToken);

        TempData["ImportResult"] =
            $"Fetched {result.Fetched}, imported {result.Imported} new, " +
            $"skipped {result.SkippedDuplicate} already on file, {result.SkippedInvalid} invalid.";

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:int}/delete")]
    [Microsoft.AspNetCore.Mvc.ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var report = await db.Reports.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (report is not null)
        {
            report.Status = ReportStatus.Removed;
            await db.SaveChangesAsync(cancellationToken);
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:int}/restore")]
    [Microsoft.AspNetCore.Mvc.ValidateAntiForgeryToken]
    public async Task<IActionResult> Restore(int id, CancellationToken cancellationToken)
    {
        var report = await db.Reports.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (report is not null)
        {
            report.Status = ReportStatus.Active;
            await db.SaveChangesAsync(cancellationToken);
        }

        return RedirectToAction(nameof(Index));
    }
}
