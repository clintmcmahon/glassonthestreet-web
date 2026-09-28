using GlassOnTheStreet.Web.Models;

namespace GlassOnTheStreet.Web.Services;

public record ReportStats(int Count, int PriorCount, double? PercentChange);

public record NeighborhoodCount(string Name, int Count);

public record TimeOfDayCount(string Bucket, int Count);

public record WardCount(int Ward, int Count);

public record ReportBreakdown(
    IReadOnlyList<NeighborhoodCount> TopNeighborhoods,
    IReadOnlyList<TimeOfDayCount> TimeOfDay,
    IReadOnlyList<WardCount> TopWards);

// A standing structural number, not a trend, so it's computed over all
// resident reports that answered the question -- not scoped to a date
// range the way ReportStats is. Backs the site's core argument: nobody
// (including MPD) has an accurate count of these because most never get
// reported.
public record PoliceReportingGap(int RespondedCount, double? PercentUnreported);

public record MonthlyCount(string MonthLabel, int Count);

public record CategoryCount(string Category, int Count);

public record YearlyCount(int Year, int Count);

/// <summary>
/// Shared behind the JSON API (ReportsApiController, for client-side
/// filtering) and the page controllers (for server-rendering real numbers
/// into the initial HTML -- a crawler or agent that doesn't execute
/// JavaScript should still see accurate current counts, not an empty
/// placeholder).
/// </summary>
public interface IReportStatsService
{
    Task<ReportStats> GetStatsAsync(DateOnly? from, DateOnly? to, CancellationToken cancellationToken = default);

    Task<ReportBreakdown> GetBreakdownAsync(DateOnly? from, DateOnly? to, CancellationToken cancellationToken = default);

    Task<PoliceReportingGap> GetPoliceReportingGapAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MonthlyCount>> GetMonthlyTrendAsync(int months, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CategoryCount>> GetCategoryCountsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Year-by-year counts for a single MPD-imported IncidentType, from
    /// startYear through the current year. Used for the homepage's
    /// "how these categories have trended since 2021" charts -- our own
    /// data, not a third-party source.
    /// </summary>
    Task<IReadOnlyList<YearlyCount>> GetYearlyCountsAsync(
        IncidentType incidentType, int startYear, CancellationToken cancellationToken = default);
}
