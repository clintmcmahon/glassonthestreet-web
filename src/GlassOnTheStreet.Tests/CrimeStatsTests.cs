using GlassOnTheStreet.Web.Services;
using Xunit;

namespace GlassOnTheStreet.Tests;

/// <summary>Serves a hand-built dataset in place of the database.</summary>
internal sealed class FakeIncidentCache(IncidentRow[] rows, DateOnly through) : IncidentDataCache(null!, null!)
{
    public override Task<IncidentDataset> GetAsync(CancellationToken cancellationToken = default)
    {
        var names = rows.Where(r => r.Neighborhood is not null).Select(r => r.Neighborhood!).Distinct().OrderBy(n => n).ToList();
        return Task.FromResult(new IncidentDataset(
            rows, through,
            names.ToDictionary(n => AreaSlug.For(n), n => n),
            names,
            rows.Where(r => r.Ward > 0).Select(r => (int)r.Ward).Distinct().OrderBy(w => w).ToList()));
    }
}

internal sealed class FakePopulation : IPopulationService
{
    public int CityTotal => 10_000;

    public int MinimumForRate => 1000;

    public string Source => "test";

    public int? ForNeighborhood(string name) => name switch { "Alpha" => 4_000, "Beta" => 500, _ => null };

    public int? ForWard(int ward) => ward == 1 ? 5_000 : null;
}

public class CrimeStatsTests
{
    private static int Index(string key) => CrimeGroups.IndexOf(key);

    private static IncidentRow Row(int id, string date, string group, string? hood = "Alpha", int ward = 1, short count = 1, byte hour = 12) =>
        new(id, DateOnly.Parse(date), hour, (byte)Index(group), count, hood, (byte)ward, 44.95f, -93.25f);

    // Data runs through 2026-09-30. 2025 has one row after that date, which must not count in the same-period total.
    private static readonly IncidentRow[] Rows =
    [
        Row(1, "2026-02-01", "agg-assault", count: 2),
        Row(2, "2026-09-30", "agg-assault"),
        Row(3, "2025-02-01", "agg-assault"),
        Row(4, "2025-10-15", "agg-assault"),
        Row(5, "2026-03-05", "shots-fired", count: 7),
        Row(6, "2026-03-06", "domestic-agg-assault", count: 3),
        Row(7, "2026-04-01", "burglary", hood: "Beta", ward: 2),
        Row(8, "2025-04-01", "burglary", hood: "Beta", ward: 2),
        Row(9, "2026-05-05", "burglary", hood: null, ward: 0),
    ];

    private static CrimeStatsService Service() =>
        new(new FakeIncidentCache(Rows, new DateOnly(2026, 9, 30)), new FakePopulation());

    [Fact]
    public async Task SamePeriod_CountsOffensesByTheirCountAndExcludesDaysAfterTheCutoff()
    {
        var data = await Service().GetCrimeAsync(new CrimeFilter("agg-assault", null, null));

        Assert.Equal(2026, data.CurrentYear);
        // 2026: 2 (Feb) + 1 (Sep 30); 2025: only Feb 1, since Oct 15 is after Sep 30.
        Assert.Equal(3, data.SamePeriod[^1]);
        Assert.Equal(1, data.SamePeriod[^2]);
    }

    [Fact]
    public async Task AllCrimes_LeavesOutShotsFiredAndSubsetRows()
    {
        var data = await Service().GetCrimeAsync(new CrimeFilter(null, null, null));

        // 3 aggravated assaults + 1 + 1 burglaries (the unplaced one counts citywide); not the 7 + 3 non-crime rows.
        Assert.Equal(3 + 1 + 1, data.SamePeriod[^1]);
        var metrics = data.Metrics.ToDictionary(m => m.Key, m => m.Counts[^1]);
        Assert.Equal(7, metrics["shots-fired"]);
        Assert.Equal(3, metrics["domestic-agg-assault"]);
        Assert.All(data.Groups, g => Assert.True(g.IsCrime));
    }

    [Fact]
    public async Task Monthly_MarksMonthsAfterTheDataAsNull()
    {
        var data = await Service().GetCrimeAsync(new CrimeFilter(null, null, null));
        var current = data.Monthly[^1];

        Assert.NotNull(current[8]); // September
        Assert.Null(current[9]); // October has no data yet
        Assert.Equal(2 + 0 + 0, current[1]); // February: 2 assaults
    }

    [Fact]
    public async Task AreaFilter_ScopesCountsAndUsesTheAreasPopulationForRates()
    {
        var data = await Service().GetCrimeAsync(new CrimeFilter(null, "Alpha", null));

        Assert.Equal(3, data.SamePeriod[^1]);
        Assert.Equal(4_000, data.Population);
        Assert.Equal(0.8, data.RatePerThousand); // 3 per 4,000 residents = 0.75, rounded to one decimal
        Assert.Equal("Alpha", data.ScopeLabel);
    }

    [Fact]
    public async Task Rates_AreWithheldBelowTheMinimumPopulation()
    {
        var data = await Service().GetCrimeAsync(new CrimeFilter(null, "Beta", null));

        Assert.Equal(1, data.SamePeriod[^1]);
        Assert.Null(data.RatePerThousand);
    }

    [Fact]
    public async Task Rankings_AreOrderedByCountAndCarryRates()
    {
        var data = await Service().GetCrimeAsync(new CrimeFilter(null, null, null));

        Assert.Equal("Alpha", data.Neighborhoods[0].Name);
        Assert.Equal(3, data.Neighborhoods[0].Counts[^1]);
        Assert.Equal("/neighborhoods/alpha", data.Neighborhoods[0].Url);
        Assert.Equal(0.8, data.Neighborhoods[0].RatePerThousand);
        Assert.Null(data.Neighborhoods.Single(n => n.Name == "Beta").RatePerThousand);
        Assert.Contains(data.Wards, w => w.Name == "Ward 1" && w.RatePerThousand == 0.6);
    }

    [Fact]
    public async Task Compare_ReturnsASeriesPerKnownAreaAndIgnoresUnknownOnes()
    {
        var compare = await Service().GetCompareAsync(["minneapolis", "alpha", "ward-2", "nope"], null);

        Assert.Equal(["minneapolis", "alpha", "ward-2"], compare.Series.Select(s => s.Key).ToArray());
        Assert.Equal(5, compare.Series[0].Counts[^1]);
        Assert.Equal(3, compare.Series[1].Counts[^1]);
        Assert.Equal(1, compare.Series[2].Counts[^1]);
        Assert.Equal("All crimes", compare.GroupLabel);
    }

    [Fact]
    public async Task NeighborhoodSummary_RanksByCountAndRate()
    {
        var summary = await Service().GetNeighborhoodSummaryAsync("alpha");

        Assert.NotNull(summary);
        Assert.Equal(1, summary!.Rank);
        Assert.Equal(2, summary.RankedOf);
        Assert.Equal(1, summary.RateRank); // Beta has too few residents to be rated
        Assert.Equal(1, summary.RateRankedOf);
        Assert.Null(await Service().GetNeighborhoodSummaryAsync("unknown"));
    }

    [Fact]
    public async Task HourWeekday_CountsLastTwelveMonthsByWeekdayAndHour()
    {
        var rows = new[]
        {
            Row(1, "2026-09-28", "agg-assault", hour: 19), // a Monday
            Row(2, "2026-09-28", "agg-assault", hour: 19, count: 2),
            Row(3, "2024-01-01", "agg-assault", hour: 19), // outside the window
        };
        var service = new CrimeStatsService(new FakeIncidentCache(rows, new DateOnly(2026, 9, 30)), new FakePopulation());

        var data = await service.GetCrimeAsync(new CrimeFilter(null, null, null));

        Assert.Equal(3, data.HourWeekday[0][19]);
        Assert.Equal(3, data.HourWeekdayTotal);
    }
}

public class MpdAddressPrettyTests
{
    [Theory]
    [InlineData("0048XX 13TH AVE S", "4800 block of 13th Ave S")]
    [InlineData("0008XX EMERSON AVE N", "800 block of Emerson Ave N")]
    [InlineData("0006XX EAST MINNEHAHA PKWY", "600 block of East Minnehaha Pkwy")]
    [InlineData("4TH ST N / 1ST AVE N", "4th St N & 1st Ave N")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Pretty_ReadsLikeAStreetAddress(string? input, string expected)
    {
        Assert.Equal(expected, MpdAddress.Pretty(input));
    }
}

public class CsvEscapeTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("Steven's Square - Loring Heights", "Steven's Square - Loring Heights")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    public void Escape_QuotesOnlyWhenNeeded(string input, string expected)
    {
        Assert.Equal(expected, CsvExportService.Escape(input));
    }
}
