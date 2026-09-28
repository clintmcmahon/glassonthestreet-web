using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using GlassOnTheStreet.Web.Models;
using GlassOnTheStreet.Web.Services;

namespace GlassOnTheStreet.Web.Controllers;

public class HomeController(IReportStatsService statsService) : Controller
{
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        // Server-rendered so the real, current count is in the initial HTML
        // -- a crawler or agent that doesn't execute JavaScript should see
        // today's number, not an empty placeholder, and it's accurate as
        // of each request without needing a client-side re-fetch.
        var stats = await statsService.GetStatsAsync(from: null, to: null, cancellationToken);
        var reportingGap = await statsService.GetPoliceReportingGapAsync(cancellationToken);
        var trend = await statsService.GetMonthlyTrendAsync(6, cancellationToken);
        var categories = await statsService.GetCategoryCountsAsync(cancellationToken);

        // Yearly trend for the homepage's "how these categories have
        // trended since 2021" charts -- our own imported MPD data, not a
        // third-party source.
        const int trendStartYear = 2021;
        var theftFromVehicleTrend = await statsService.GetYearlyCountsAsync(IncidentType.Unknown, trendStartYear, cancellationToken);
        var propertyDamageTrend = await statsService.GetYearlyCountsAsync(IncidentType.PropertyDamage, trendStartYear, cancellationToken);
        var vehicleTheftTrend = await statsService.GetYearlyCountsAsync(IncidentType.VehicleStolen, trendStartYear, cancellationToken);

        return View(new HomePageViewModel(
            stats, reportingGap, trend, categories,
            theftFromVehicleTrend, propertyDamageTrend, vehicleTheftTrend));
    }

    [HttpGet("privacy")]
    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}

public record HomePageViewModel(
    ReportStats Stats,
    PoliceReportingGap ReportingGap,
    IReadOnlyList<MonthlyCount> Trend,
    IReadOnlyList<CategoryCount> Categories,
    IReadOnlyList<YearlyCount> TheftFromVehicleTrend,
    IReadOnlyList<YearlyCount> PropertyDamageTrend,
    IReadOnlyList<YearlyCount> VehicleTheftTrend);
