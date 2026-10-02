using GlassOnTheStreet.Web.Models;

namespace GlassOnTheStreet.Web.Services;

/// <summary>Slice of the MPD data a dashboard request is scoped to. All null means everything.</summary>
public record TrendFilter(IncidentType? Category, string? Neighborhood, int? Ward);

/// <param name="Counts">Same-period count for each year, aligned with TrendsData.Years.</param>
/// <param name="ChangeVsPrior">Percent change of the current year vs the prior year; null when the prior count is zero.</param>
/// <param name="Url">The area's own page, e.g. /neighborhoods/whittier.</param>
/// <param name="RatePerThousand">This year's count per 1,000 residents; null when residents are unknown or too few.</param>
public record AreaTrend(string Name, int[] Counts, double? ChangeVsPrior, string Url, double? RatePerThousand = null, int? Population = null);

/// <param name="Monthly">Per year, 12 monthly counts; months after the data's end are null.</param>
/// <param name="SamePeriod">Per year, the count from Jan 1 through the same month and day as Through.</param>
public record TrendsData(
    int[] Years,
    int CurrentYear,
    DateOnly Through,
    int?[][] Monthly,
    int[] SamePeriod,
    IReadOnlyList<AreaTrend> Neighborhoods,
    IReadOnlyList<AreaTrend> Wards,
    IReadOnlyList<string> NeighborhoodOptions,
    IReadOnlyList<int> WardOptions);

/// <param name="Counts">Same-period counts per year, aligned with the page's Years.</param>
public record CategoryTrend(string Label, int[] Counts);

public record BucketCount(string Label, int Count);

public record RelatedArea(string Name, string Url, int Count);

/// <summary>Everything a single neighborhood or ward page shows.</summary>
/// <param name="Kind">"neighborhood" or "ward".</param>
/// <param name="Rank">Rank by reports so far this year among all areas of the same kind.</param>
/// <param name="TotalReports">All reports for the area since 2019 (used to keep thin pages out of search).</param>
/// <param name="Ward">For a neighborhood, the ward most of its reports fall in.</param>
public record AreaPageData(
    string Kind,
    string Name,
    string Slug,
    string Url,
    TrendsData Trends,
    IReadOnlyList<CategoryTrend> Categories,
    IReadOnlyList<BucketCount> TimeOfDay,
    int Rank,
    int RankedOf,
    int TotalReports,
    int? Ward,
    IReadOnlyList<RelatedArea> Related);

/// <summary>Every neighborhood and ward, for the /neighborhoods index and the sitemap.</summary>
public record AreaIndexData(
    int[] Years,
    DateOnly Through,
    IReadOnlyList<AreaTrend> Neighborhoods,
    IReadOnlyList<AreaTrend> Wards);

/// <summary>
/// Everything behind /trends and the neighborhood and ward pages. Built from
/// the MPD-imported reports only (resident reports are a different, much
/// smaller dataset), counted by ReportedDate like the homepage's yearly charts.
/// </summary>
public interface ITrendsService
{
    Task<TrendsData> GetTrendsAsync(TrendFilter filter, CancellationToken cancellationToken = default);

    /// <summary>Null when no neighborhood has that slug.</summary>
    Task<AreaPageData?> GetNeighborhoodAsync(string slug, CancellationToken cancellationToken = default);

    /// <summary>Null when the ward has no reports.</summary>
    Task<AreaPageData?> GetWardAsync(int ward, CancellationToken cancellationToken = default);

    Task<AreaIndexData> GetAreaIndexAsync(CancellationToken cancellationToken = default);
}
