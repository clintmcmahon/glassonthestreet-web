using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using GlassOnTheStreet.Web.Models;
using GlassOnTheStreet.Web.Services;

namespace GlassOnTheStreet.Web.Controllers;

public class HomeController(IReportStatsService statsService, ISyncStatusService syncStatusService, ITrendsService trendsService, ICrimeStatsService crimeStats) : Controller
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

        // "What's on the map" is scoped to the current calendar year, not
        // all-time -- with 117K+ rows going back to 2020, an all-time
        // breakdown doesn't tell you what's happening now.
        var yearStart = new DateOnly(DateTime.UtcNow.Year, 1, 1);
        // MPD categories and resident reports come from different places and are listed as separate
        // rows, never added into one total: a resident who also called the police is in both.
        var mpdCategories = await statsService.GetCategoryCountsAsync(yearStart, to: null, cancellationToken);
        var residentCategories = await statsService.GetResidentCategoryCountsAsync(yearStart, to: null, cancellationToken);
        var categories = mpdCategories.Concat(residentCategories).ToList();

        // Yearly trend for the homepage's "how these categories have
        // trended since 2019" charts -- our own imported MPD data, not a
        // third-party source.
        const int trendStartYear = 2019;
        var theftFromVehicleTrend = await statsService.GetYearlyCountsAsync(IncidentType.Unknown, trendStartYear, cancellationToken);
        var propertyDamageTrend = await statsService.GetYearlyCountsAsync(IncidentType.PropertyDamage, trendStartYear, cancellationToken);
        var vehicleTheftTrend = await statsService.GetYearlyCountsAsync(IncidentType.VehicleStolen, trendStartYear, cancellationToken);

        var lastSyncedAt = await syncStatusService.GetLastSyncedAtAsync(cancellationToken);

        // Year-to-date comparison and the top areas, from the same cached
        // MPD dataset that powers /trends.
        var trends = await trendsService.GetTrendsAsync(new TrendFilter(null, null, null), cancellationToken);
        var residentReports = await statsService.GetResidentReportCountAsync(trends.Through.AddDays(-29), to: null, cancellationToken);

        // All offense types, the same numbers /crime leads with.
        var crime = await crimeStats.GetCrimeAsync(new CrimeFilter(null, null, null), cancellationToken: cancellationToken);

        return View(new HomePageViewModel(
            stats, reportingGap, trend, categories,
            theftFromVehicleTrend, propertyDamageTrend, vehicleTheftTrend, lastSyncedAt, trends, residentReports, crime));
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
    IReadOnlyList<YearlyCount> VehicleTheftTrend,
    DateTime? MpdLastSyncedAt,
    TrendsData Trends,
    int ResidentReportsLast30Days,
    CrimeData Crime);
