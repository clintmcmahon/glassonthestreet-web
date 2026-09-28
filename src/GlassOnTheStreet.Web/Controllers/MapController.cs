using GlassOnTheStreet.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GlassOnTheStreet.Web.Controllers;

[Route("map")]
public class MapController(IReportStatsService statsService, ISyncStatusService syncStatusService) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        // Server-rendered for the same reason as the homepage: a crawler or
        // agent fetching this page without running JavaScript should still
        // see the real current count and breakdown, not empty placeholders.
        // The default view (last 30 days) matches the page's default JS
        // filter state, so there's no visible flash when it re-fetches.
        var stats = await statsService.GetStatsAsync(from: null, to: null, cancellationToken);
        var breakdown = await statsService.GetBreakdownAsync(from: null, to: null, cancellationToken);
        var reportingGap = await statsService.GetPoliceReportingGapAsync(cancellationToken);
        var lastSyncedAt = await syncStatusService.GetLastSyncedAtAsync(cancellationToken);
        return View(new MapPageViewModel(stats, breakdown, reportingGap, lastSyncedAt));
    }
}

public record MapPageViewModel(ReportStats Stats, ReportBreakdown Breakdown, PoliceReportingGap ReportingGap, DateTime? MpdLastSyncedAt);
