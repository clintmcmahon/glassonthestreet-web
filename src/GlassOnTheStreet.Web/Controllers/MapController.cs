using GlassOnTheStreet.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GlassOnTheStreet.Web.Controllers;

public record MapPageViewModel(MapSummary Summary, PoliceReportingGap ReportingGap, DateTime? MpdLastSyncedAt);

[Route("map")]
public class MapController(
    MapDataService mapData, IncidentDataCache dataCache, IReportStatsService statsService, ISyncStatusService syncStatusService) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        // Server-rendered for the same reason as the homepage: a crawler or agent fetching this
        // page without running JavaScript should still see the real numbers, not empty placeholders.
        // The default view (car-related offenses, last 30 days) matches the page's default
        // JavaScript state, so there's no visible flash when it re-fetches.
        var dataset = await dataCache.GetAsync(cancellationToken);
        var (from, to) = MapDataService.ResolveRange("30", null, null, dataset.Through);
        var summary = await mapData.GetSummaryAsync(new MapFilter(MapDataService.CarGroup, from, to), cancellationToken);
        var reportingGap = await statsService.GetPoliceReportingGapAsync(cancellationToken);
        var lastSyncedAt = await syncStatusService.GetLastSyncedAtAsync(cancellationToken);
        return View(new MapPageViewModel(summary, reportingGap, lastSyncedAt));
    }
}
