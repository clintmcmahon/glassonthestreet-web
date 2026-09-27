using GlassOnTheStreet.Web.Data;
using GlassOnTheStreet.Web.Infrastructure;
using GlassOnTheStreet.Web.Models;
using GlassOnTheStreet.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GlassOnTheStreet.Web.Controllers.Admin;

public record AdminIndexViewModel(List<Report> Pending, List<Report> Recent);

[Route("admin")]
[AdminBasicAuth]
public class AdminController(GlassOnTheStreetContext db, IOfficialDataImportService importService) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        // Oldest-first so a submission never quietly rots at the bottom of
        // the queue behind newer ones.
        var pending = await db.Reports
            .Where(r => r.Status == ReportStatus.Pending)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(cancellationToken);

        // Everything else, most recent first -- pending items have their
        // own section above, so they're excluded here to avoid duplicates.
        var recent = await db.Reports
            .Include(r => r.Flags)
            .Where(r => r.Status != ReportStatus.Pending)
            .OrderByDescending(r => r.CreatedAt)
            .Take(200)
            .ToListAsync(cancellationToken);

        return View(new AdminIndexViewModel(pending, recent));
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

    // One-time cleanup for the pre-2026-09-27 import bug: an earlier version
    // of MinneapolisOpenDataImportService imported MPD's "Destruction/
    // Damage/Vandalism of Property" category, which isn't vehicle-specific
    // (see the doc comment on that class). This soft-removes those rows so
    // they stop showing up as car break-ins. Safe to click more than once --
    // it only ever touches currently-Active rows with that exact Offense
    // text, so a second run affects zero rows.
    [HttpPost("cleanup-legacy-vandalism")]
    [Microsoft.AspNetCore.Mvc.ValidateAntiForgeryToken]
    public async Task<IActionResult> CleanupLegacyVandalism(CancellationToken cancellationToken)
    {
        var affected = await db.Reports
            .Where(r => r.SourceType == SourceType.OfficialImport
                && r.Status == ReportStatus.Active
                && r.Offense == "Destruction/Damage/Vandalism of Property")
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ReportStatus.Removed), cancellationToken);

        TempData["ImportResult"] = $"Removed {affected} legacy non-vehicle vandalism rows.";

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:int}/approve")]
    [Microsoft.AspNetCore.Mvc.ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id, CancellationToken cancellationToken)
    {
        var report = await db.Reports.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (report is not null && report.Status == ReportStatus.Pending)
        {
            report.Status = ReportStatus.Active;
            await db.SaveChangesAsync(cancellationToken);
        }

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
