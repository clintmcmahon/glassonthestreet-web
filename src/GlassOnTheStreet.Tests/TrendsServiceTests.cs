using GlassOnTheStreet.Web.Models;
using GlassOnTheStreet.Web.Services;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace GlassOnTheStreet.Tests;

/// <summary>The car-category pages read the same data as the all-offense pages, so the two must agree.</summary>
public class TrendsServiceTests
{
    private static IncidentRow Row(int id, string date, string group, string? hood = "Alpha", int ward = 1, short count = 1, byte hour = 12) =>
        new(id, DateOnly.Parse(date), hour, (byte)CrimeGroups.IndexOf(group), count, hood, (byte)ward, 44.95f, -93.25f);

    private static readonly IncidentRow[] Rows =
    [
        Row(1, "2026-02-01", "theft-from-vehicle", count: 3, hour: 2),
        Row(2, "2025-02-01", "theft-from-vehicle", hour: 8),
        Row(3, "2026-03-01", "vehicle-theft", hour: 19),
        Row(4, "2026-03-02", "vandalism", hood: "Beta", ward: 2),
        Row(5, "2026-03-03", "agg-assault"), // not a car category
        Row(6, "2026-03-04", "shots-fired", count: 9), // not a crime at all
    ];

    private static (TrendsService Trends, CrimeStatsService Crime) Services()
    {
        var cache = new FakeIncidentCache(Rows, new DateOnly(2026, 9, 30));
        return (new TrendsService(cache, new MemoryCache(new MemoryCacheOptions())), new CrimeStatsService(cache, new FakePopulation()));
    }

    [Fact]
    public async Task OnlyTheFourCarCategoriesAreCounted_WeightedByOffenseCount()
    {
        var (trends, _) = Services();

        var all = await trends.GetTrendsAsync(new TrendFilter(null, null, null));
        var cars = await trends.GetTrendsAsync(new TrendFilter(IncidentType.Unknown, null, null));

        Assert.Equal(3 + 1 + 1, all.SamePeriod[^1]); // 3 break-in offenses + a stolen car + vandalism; not the assault or shots-fired
        Assert.Equal(3, cars.SamePeriod[^1]);
        Assert.Equal(1, cars.SamePeriod[^2]);
    }

    [Fact]
    public async Task CarCategoryCountsMatchTheCrimePagesForTheSameGroup()
    {
        var (trends, crime) = Services();

        var fromTrends = await trends.GetTrendsAsync(new TrendFilter(IncidentType.Unknown, null, null));
        var fromCrime = await crime.GetCrimeAsync(new CrimeFilter("theft-from-vehicle", null, null));

        Assert.Equal(fromCrime.SamePeriod, fromTrends.SamePeriod);
        Assert.Equal(fromCrime.Through, fromTrends.Through);
    }

    [Fact]
    public async Task NeighborhoodPage_BucketsTheHourIntoTimeOfDayAndSumsOffenses()
    {
        var (trends, _) = Services();

        var page = await trends.GetNeighborhoodAsync("alpha");

        Assert.NotNull(page);
        var buckets = page!.TimeOfDay.ToDictionary(b => b.Label, b => b.Count);
        Assert.Equal(3, buckets["Overnight"]); // the 2 AM break-ins, three offenses
        Assert.Equal(1, buckets["Evening"]); // the 7 PM vehicle theft
        Assert.Equal(5, page.TotalReports); // 3 + 1 (2025) + 1, all years
        Assert.Null(await trends.GetNeighborhoodAsync("nope"));
    }

    [Fact]
    public async Task YearlyCounts_UseTheSameDatasetAndProjectFromTheFeedsLastDate()
    {
        var cache = new FakeIncidentCache(Rows, new DateOnly(2026, 9, 30));
        var stats = new ReportStatsService(null!, cache, new MapDataService(cache));

        var yearly = await stats.GetYearlyCountsAsync(IncidentType.Unknown, 2025);

        Assert.Equal([2025, 2026], yearly.Select(y => y.Year).ToArray());
        Assert.Equal(1, yearly[0].Count);
        Assert.Equal(3, yearly[1].Count);
        Assert.Equal(new DateOnly(2026, 9, 30), yearly[1].AsOf);
    }
}

/// <summary>
/// The headline figures are MPD data only. These run with no database at all, which is the point:
/// a count that never touches the Reports table can't include a resident report.
/// </summary>
public class HeadlineFiguresAreMpdOnlyTests
{
    private static IncidentRow Row(int id, string date, string group, string? hood = "Alpha", int ward = 1, short count = 1) =>
        new(id, DateOnly.Parse(date), 12, (byte)CrimeGroups.IndexOf(group), count, hood, (byte)ward, 44.95f, -93.25f);

    private static ReportStatsService Stats(params IncidentRow[] rows)
    {
        var cache = new FakeIncidentCache(rows, new DateOnly(2026, 9, 30));
        return new ReportStatsService(null!, cache, new MapDataService(cache));
    }

    [Fact]
    public async Task Stats_CountOnlyTheCarGroupsAndComparePriorThirtyDays()
    {
        var stats = Stats(
            Row(1, "2026-09-10", "theft-from-vehicle", count: 6),
            Row(2, "2026-09-10", "vandalism", count: 2),
            Row(3, "2026-09-10", "agg-assault", count: 50), // not a car group
            Row(4, "2026-08-10", "vehicle-theft", count: 4)); // the 30 days before

        var result = await stats.GetStatsAsync(null, null);

        Assert.Equal(8, result.Count);
        Assert.Equal(4, result.PriorCount);
        Assert.Equal(100, result.PercentChange);
    }

    [Fact]
    public async Task MonthlyTrend_IsCompleteMonthsOfCarOffensesOnly()
    {
        var stats = Stats(
            Row(1, "2026-08-05", "theft-from-vehicle", count: 3),
            Row(2, "2026-07-05", "parts-theft", count: 2),
            Row(3, "2026-07-06", "burglary", count: 9), // not a car group
            Row(4, "2026-09-29", "theft-from-vehicle", count: 99)); // the data ends on Sep 30, the last day, so September counts as complete

        var trend = await stats.GetMonthlyTrendAsync(3);

        Assert.Equal(["Jul 2026", "Aug 2026", "Sep 2026"], trend.Select(t => t.MonthLabel).ToArray());
        Assert.Equal([2, 3, 99], trend.Select(t => t.Count).ToArray());
    }

    [Fact]
    public async Task Categories_AreTheFourMpdCarGroupsUnderTheirOldNames()
    {
        var stats = Stats(
            Row(1, "2026-03-01", "theft-from-vehicle", count: 5),
            Row(2, "2026-03-01", "vehicle-theft", count: 2),
            Row(3, "2026-03-01", "agg-assault", count: 40));

        var categories = (await stats.GetCategoryCountsAsync(new DateOnly(2026, 1, 1), null)).ToDictionary(c => c.Category, c => c.Count);

        Assert.Equal(5, categories["Unknown"]);
        Assert.Equal(2, categories["VehicleStolen"]);
        Assert.False(categories.ContainsKey("WindowSmashed")); // residents' categories come from the separate resident method
        Assert.False(categories.ContainsKey("Rifled"));
    }

    [Fact]
    public async Task Breakdown_ComesFromTheSameMpdWindow()
    {
        var stats = Stats(
            Row(1, "2026-09-10", "theft-from-vehicle", hood: "Alpha", ward: 1, count: 3),
            Row(2, "2026-09-10", "theft-from-vehicle", hood: "Beta", ward: 2));

        var breakdown = await stats.GetBreakdownAsync(null, null);

        Assert.Equal("Alpha", breakdown.TopNeighborhoods[0].Name);
        Assert.Equal(3, breakdown.TopNeighborhoods[0].Count);
        Assert.Equal(1, breakdown.TopWards[0].Ward);
        Assert.Equal(4, breakdown.TimeOfDay.Sum(t => t.Count));
    }
}
