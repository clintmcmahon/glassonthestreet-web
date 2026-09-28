using GlassOnTheStreet.Web.Data;
using GlassOnTheStreet.Web.Infrastructure;
using GlassOnTheStreet.Web.Models;
using GlassOnTheStreet.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GlassOnTheStreet.Web.Controllers.Admin;

public record AdminIndexViewModel(
    List<Report> Pending,
    List<Report> Recent,
    bool ImportRunning,
    int Page,
    int TotalPages,
    int TotalRecent,
    DateTime? MpdLastSyncedAt);

[Route("admin")]
[AdminBasicAuth]
public class AdminController(
    GlassOnTheStreetContext db,
    IOfficialDataImportService importService,
    ISyncStatusService syncStatusService) : Controller
{
    private const int PageSize = 100;

    [HttpGet("")]
    public async Task<IActionResult> Index([FromQuery] int page, CancellationToken cancellationToken)
    {
        // Oldest-first so a submission never quietly rots at the bottom of
        // the queue behind newer ones.
        var pending = await db.Reports
            .Where(r => r.Status == ReportStatus.Pending)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(cancellationToken);

        // Everything else -- pending items have their own section above,
        // so they're excluded here to avoid duplicates. Ordered by
        // ReportedDate (when the incident happened), not CreatedAt (when
        // we happened to insert the row): a bulk import inserts rows in
        // whatever order the source feed paginates them, which is not
        // necessarily newest-incident-first, so CreatedAt ordering could
        // put 2020 at the top of a "most recent" list. Id as a tiebreaker
        // keeps ordering stable across pages when many rows share a date.
        var recentQuery = db.Reports
            .Include(r => r.Flags)
            .Where(r => r.Status != ReportStatus.Pending);

        var totalRecent = await recentQuery.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalRecent / (double)PageSize));
        var currentPage = Math.Clamp(page <= 0 ? 1 : page, 1, totalPages);

        var recent = await recentQuery
            .OrderByDescending(r => r.ReportedDate)
            .ThenByDescending(r => r.Id)
            .Skip((currentPage - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync(cancellationToken);

        var lastSyncedAt = await syncStatusService.GetLastSyncedAtAsync(cancellationToken);

        return View(new AdminIndexViewModel(pending, recent, importService.IsRunning, currentPage, totalPages, totalRecent, lastSyncedAt));
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
