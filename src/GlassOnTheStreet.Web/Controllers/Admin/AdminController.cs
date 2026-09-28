using GlassOnTheStreet.Web.Data;
using GlassOnTheStreet.Web.Infrastructure;
using GlassOnTheStreet.Web.Models;
using GlassOnTheStreet.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GlassOnTheStreet.Web.Controllers.Admin;

public record AdminIndexViewModel(List<Report> Pending, List<Report> Recent, bool ImportRunning);

[Route("admin")]
[AdminBasicAuth]
public class AdminController(
    GlassOnTheStreetContext db,
    IOfficialDataImportService importService,
    IServiceScopeFactory scopeFactory,
    ILogger<AdminController> logger) : Controller
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

        return View(new AdminIndexViewModel(pending, recent, importService.IsRunning));
    }

    // A full-history import (large lookbackDays) can take several minutes --
    // long past nginx's default proxy read timeout, which is what was
    // showing up as a browser-side "request timed out" even though the
    // import itself was fine. Kicked off in the background instead of
    // awaited inline, using its own DI scope since the request's scoped
    // services (db, importService) are disposed the moment this action
    // returns. Safe to fire without awaiting: ImportAsync's own semaphore
    // already serializes this against the daily background sync and any
    // other concurrent import.
    [HttpPost("import")]
    [Microsoft.AspNetCore.Mvc.ValidateAntiForgeryToken]
    public IActionResult Import([FromForm] int lookbackDays)
    {
        var days = lookbackDays <= 0 ? 180 : lookbackDays;

        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            var scopedImportService = scope.ServiceProvider.GetRequiredService<IOfficialDataImportService>();
            try
            {
                var result = await scopedImportService.ImportAsync(days, CancellationToken.None);
                logger.LogInformation(
                    "Admin-triggered import finished: fetched {Fetched}, imported {Imported}, " +
                    "skipped {SkippedDuplicate} duplicate / {SkippedInvalid} invalid",
                    result.Fetched, result.Imported, result.SkippedDuplicate, result.SkippedInvalid);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Admin-triggered import failed");
            }
        });

        TempData["ImportResult"] =
            $"Import started in the background for the last {days} days. A large lookback can take " +
            "several minutes -- refresh this page to check progress; new reports appear on the map as they're saved.";

        return RedirectToAction(nameof(Index));
    }

    // "Destruction/Damage/Vandalism of Property" was originally imported as
    // generic Unknown, then removed by an earlier version of this cleanup
    // action for not being vehicle-specific, then brought back (in its own
    // IncidentType.PropertyDamage, see Report.cs and
    // MinneapolisOpenDataImportService's doc comment) at the explicit call
    // of the site's owner. Import dedupes by MPD's case number, so a fresh
    // import will never re-touch rows already on file -- this one-time
    // action reclassifies and restores them instead: any row soft-removed
    // by the old cleanup comes back to Active, and any row still labeled
    // Unknown from before PropertyDamage existed gets relabeled. Safe to
    // click more than once -- a second run affects zero rows.
    [HttpPost("reclassify-property-damage")]
    [Microsoft.AspNetCore.Mvc.ValidateAntiForgeryToken]
    public async Task<IActionResult> ReclassifyPropertyDamage(CancellationToken cancellationToken)
    {
        var affected = await db.Reports
            .Where(r => r.SourceType == SourceType.OfficialImport
                && r.Offense == "Destruction/Damage/Vandalism of Property"
                && (r.IncidentType != IncidentType.PropertyDamage || r.Status != ReportStatus.Active))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.IncidentType, IncidentType.PropertyDamage)
                .SetProperty(r => r.Status, ReportStatus.Active), cancellationToken);

        TempData["ImportResult"] = $"Reclassified/restored {affected} property-damage rows.";

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
