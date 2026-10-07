using GlassOnTheStreet.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GlassOnTheStreet.Web.Controllers;

public record DataQualityStats(
    DateOnly Through,
    DateOnly LatestRecord,
    int LagDays,
    int Records,
    int Unplaced,
    DateTime? LastSyncedUtc);

/// <summary>A live snapshot of the feed's known gaps, beside a dated log of the problems found and fixed.</summary>
public class DataQualityController(IncidentDataCache dataCache, ISyncStatusService syncStatus, IConfiguration configuration) : Controller
{
    [HttpGet("data-quality")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var dataset = await dataCache.GetAsync(cancellationToken);
        var unplaced = dataset.Rows.Count(r => float.IsNaN(r.Lat));
        ViewData["ContactEmail"] = configuration["Site:ContactEmail"];
        return View(new DataQualityStats(
            dataset.Through, dataset.LatestRecord ?? dataset.Through, IncidentDataCache.LagDays,
            dataset.Rows.Length, unplaced, await syncStatus.GetLastSyncedAtAsync(cancellationToken)));
    }
}
