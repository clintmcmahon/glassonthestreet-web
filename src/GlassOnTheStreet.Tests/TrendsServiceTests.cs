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
        var stats = new ReportStatsService(null!, cache);

        var yearly = await stats.GetYearlyCountsAsync(IncidentType.Unknown, 2025);

        Assert.Equal([2025, 2026], yearly.Select(y => y.Year).ToArray());
        Assert.Equal(1, yearly[0].Count);
        Assert.Equal(3, yearly[1].Count);
        Assert.Equal(new DateOnly(2026, 9, 30), yearly[1].AsOf);
    }
}
