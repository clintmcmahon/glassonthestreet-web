namespace GlassOnTheStreet.Web.Services;

/// <param name="Group">A CrimeGroups key; null means all crimes (the non-crime metrics are never included).</param>
public record CrimeFilter(string? Group, string? Neighborhood, int? Ward);

/// <param name="Counts">Same-period count for each year, aligned with CrimeData.Years.</param>
/// <param name="RatePerThousand">This year's count per 1,000 residents of the scope; null when residents are unknown.</param>
public record GroupRow(
    string Key, string Label, string Definition, bool IsCrime, int[] Counts,
    double? ChangeVsPrior, double? ChangeVsBase, double? RatePerThousand);

/// <param name="Monthly">Per year, 12 monthly offense counts; months after the data's end are null.</param>
/// <param name="SamePeriod">Per year, the count from Jan 1 through the same month and day as Through.</param>
/// <param name="Groups">Every crime group within the neighborhood/ward scope, ignoring the group filter.</param>
/// <param name="Metrics">Shots-fired calls, gunshot victims and other rows that aren't counted as crimes.</param>
/// <param name="HourWeekday">Offense counts for the last 12 months: [weekday 0 = Monday][hour 0-23].</param>
/// <param name="RatePerThousand">This year's count per 1,000 residents of the scope.</param>
public record CrimeData(
    int[] Years,
    int CurrentYear,
    DateOnly Through,
    int?[][] Monthly,
    int[] SamePeriod,
    IReadOnlyList<AreaTrend> Neighborhoods,
    IReadOnlyList<AreaTrend> Wards,
    IReadOnlyList<GroupRow> Groups,
    IReadOnlyList<GroupRow> Metrics,
    int[][] HourWeekday,
    int HourWeekdayTotal,
    int Population,
    double? RatePerThousand,
    string ScopeLabel,
    IReadOnlyList<string> NeighborhoodOptions,
    IReadOnlyList<int> WardOptions);

/// <summary>A single area's all-crime picture plus where it ranks.</summary>
public record AreaCrimeSummary(
    CrimeData Data,
    int Rank,
    int RankedOf,
    int? RateRank,
    int? RateRankedOf,
    double? CityRatePerThousand);

/// <param name="Rates">Same-period count per 1,000 residents (2020 Census) for each year; null when residents are unknown or too few.</param>
public record CompareSeries(string Key, string Name, string Url, int? Population, int[] Counts, double?[] Rates);

public record CompareData(int[] Years, DateOnly Through, string GroupLabel, IReadOnlyList<CompareSeries> Series);

public interface ICrimeStatsService
{
    /// <param name="areaKeys">"minneapolis", "ward-N" or a neighborhood slug; unknown keys are ignored (at most four are used).</param>
    Task<CompareData> GetCompareAsync(IReadOnlyList<string> areaKeys, string? group, CancellationToken cancellationToken = default);

    /// <param name="sortByRate">Order the area rankings by offenses per 1,000 residents instead of by count.</param>
    Task<CrimeData> GetCrimeAsync(CrimeFilter filter, bool sortByRate = false, CancellationToken cancellationToken = default);

    /// <summary>Null when the neighborhood slug is unknown.</summary>
    Task<AreaCrimeSummary?> GetNeighborhoodSummaryAsync(string slug, CancellationToken cancellationToken = default);

    /// <summary>Null when the ward has no data.</summary>
    Task<AreaCrimeSummary?> GetWardSummaryAsync(int ward, CancellationToken cancellationToken = default);
}
