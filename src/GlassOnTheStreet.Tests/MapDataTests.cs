using GlassOnTheStreet.Web.Services;
using Xunit;

namespace GlassOnTheStreet.Tests;

public class MapDataTests
{
    private static readonly DateOnly Through = new(2026, 9, 30);

    // Address table: index 0 is "none", 1 is a block address.
    private static readonly string[] Addresses = ["", "0048XX 13TH AVE S", "0049XX PARK AVE"];

    private static IncidentRow Row(
        int id, string date, string group, float lat = 44.95f, float lng = -93.25f, string? hood = "Alpha", int ward = 1,
        short count = 1, byte hour = 12, int address = 1) =>
        new(id, DateOnly.Parse(date), hour, (byte)CrimeGroups.IndexOf(group), count, hood, (byte)ward, lat, lng, address);

    private static MapDataService Service(params IncidentRow[] rows) =>
        new(new FakeIncidentCache(rows, Through, Addresses));

    private static MapFilter Filter(string? group, string from = "2026-09-01", string to = "2026-09-30") =>
        new(MapDataService.NormalizeGroup(group), DateOnly.Parse(from), DateOnly.Parse(to));

    [Fact]
    public async Task Blocks_AggregateEverythingOnTheSameBlockMidpointIntoOneFeature()
    {
        var result = await Service(
            Row(1, "2026-09-02", "theft-from-vehicle", count: 3),
            Row(2, "2026-09-03", "vandalism"),
            Row(3, "2026-09-04", "theft-from-vehicle"),
            Row(4, "2026-09-05", "theft-from-vehicle", lat: 44.96f, address: 2))
            .GetBlocksAsync(Filter("car"));

        Assert.Equal(2, result.Blocks.Count);
        Assert.Equal(6, result.Total);
        var busiest = result.Blocks[0];
        Assert.Equal(5, busiest.Count);
        Assert.Equal("4800 block of 13th Ave S", busiest.Address);
        Assert.Equal("Alpha", busiest.Neighborhood);
        Assert.Equal("theft-from-vehicle:4|vandalism:1", busiest.Top);
        Assert.Equal("4900 block of Park Ave", result.Blocks[1].Address);
    }

    [Fact]
    public async Task Blocks_RespectTheDateRangeAndTheOffenseFilter()
    {
        var service = Service(
            Row(1, "2026-08-31", "burglary"),
            Row(2, "2026-09-10", "burglary"),
            Row(3, "2026-09-10", "agg-assault"),
            Row(4, "2026-09-11", "shots-fired", count: 9));

        Assert.Equal(1, (await service.GetBlocksAsync(Filter("burglary"))).Total);
        Assert.Equal(2, (await service.GetBlocksAsync(Filter("all"))).Total); // crimes only, no shots-fired
        Assert.Equal(9, (await service.GetBlocksAsync(Filter("shots-fired"))).Total);
        Assert.Equal(0, (await service.GetBlocksAsync(Filter("car"))).Total);
        Assert.Equal(1, (await service.GetBlocksAsync(new MapFilter("burglary", new DateOnly(2026, 8, 31), new DateOnly(2026, 8, 31)))).Total);
    }

    [Fact]
    public async Task Blocks_SkipRowsWithoutACoordinate()
    {
        var result = await Service(
            Row(1, "2026-09-10", "burglary", lat: float.NaN, lng: float.NaN),
            Row(2, "2026-09-10", "burglary")).GetBlocksAsync(Filter("burglary"));

        Assert.Single(result.Blocks);
        Assert.Equal(1, result.Total);
    }

    [Fact]
    public async Task Summary_ComparesWithThePeriodJustBeforeIt()
    {
        var service = Service(
            Row(1, "2026-09-10", "burglary", count: 6),
            Row(2, "2026-08-10", "burglary", count: 4), // the 30 days before Sep 1 to Sep 30
            Row(3, "2026-09-10", "agg-assault"));

        var summary = await service.GetSummaryAsync(Filter("burglary"));

        Assert.Equal(6, summary.Total);
        Assert.Equal(4, summary.PriorTotal);
        Assert.Equal(50, summary.ChangeVsPrior); // 4 to 6
    }

    [Fact]
    public async Task Summary_BucketsHoursAndRanksAreas()
    {
        var service = Service(
            Row(1, "2026-09-10", "burglary", hour: 2, hood: "Alpha", ward: 1, count: 2),
            Row(2, "2026-09-10", "burglary", hour: 8, hood: "Beta", ward: 2),
            Row(3, "2026-09-10", "burglary", hour: 23, hood: "Alpha", ward: 1),
            Row(4, "2026-09-10", "burglary", hour: 18, hood: "Alpha", ward: 1));

        var summary = await service.GetSummaryAsync(Filter("burglary"));

        var times = summary.TimeOfDay.ToDictionary(t => t.Name, t => t.Count);
        Assert.Equal(3, times["Overnight"]); // 2 AM (two offenses) and 11 PM
        Assert.Equal(1, times["Morning"]);
        Assert.Equal(1, times["Evening"]);
        Assert.Equal("Alpha", summary.Neighborhoods[0].Name);
        Assert.Equal("/neighborhoods/alpha", summary.Neighborhoods[0].Url);
        Assert.Equal("Ward 1", summary.Wards[0].Name);
        Assert.Equal("Burglary", summary.Types[0].Name);
    }

    [Theory]
    [InlineData("7", "2026-09-24", "2026-09-30")]
    [InlineData("30", "2026-09-01", "2026-09-30")]
    [InlineData("365", "2025-10-01", "2026-09-30")]
    [InlineData("ytd", "2026-01-01", "2026-09-30")]
    [InlineData("all", "2019-01-01", "2026-09-30")]
    [InlineData(null, "2026-09-01", "2026-09-30")]
    [InlineData("nonsense", "2026-09-01", "2026-09-30")]
    public void ResolveRange_CountsBackFromTheLastPublishedDate(string? range, string from, string to)
    {
        var (start, end) = MapDataService.ResolveRange(range, null, null, Through);

        Assert.Equal(DateOnly.Parse(from), start);
        Assert.Equal(DateOnly.Parse(to), end);
    }

    [Fact]
    public void ResolveRange_ClampsAnExplicitRangeToTheDataAndPutsItInOrder()
    {
        var (start, end) = MapDataService.ResolveRange(null, new DateOnly(2010, 1, 1), new DateOnly(2030, 1, 1), Through);
        Assert.Equal(new DateOnly(2019, 1, 1), start);
        Assert.Equal(Through, end);

        var (a, b) = MapDataService.ResolveRange(null, new DateOnly(2026, 9, 20), new DateOnly(2026, 9, 10), Through);
        Assert.True(a < b);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("all", "")]
    [InlineData("car", "car")]
    [InlineData("agg-assault", "agg-assault")]
    [InlineData("shots-fired", "shots-fired")]
    [InlineData("not-a-group", "")]
    public void NormalizeGroup_AcceptsOnlyKnownGroups(string? input, string expected)
    {
        Assert.Equal(expected, MapDataService.NormalizeGroup(input));
    }
}
