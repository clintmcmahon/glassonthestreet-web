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
}
