using GlassOnTheStreet.Web.Models;
using GlassOnTheStreet.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GlassOnTheStreet.Tests.DataAccuracy;

/// <summary>
/// Every figure the site computes, checked against numbers worked out by hand from the source feed
/// (see <see cref="Oracle"/>). Run across many filters so one passing case can't hide a broken one.
/// </summary>
[Collection("world")]
public class ServiceAccuracyTests(World world) : IDisposable
{
    private readonly IServiceScope scope = world.Factory.Services.CreateScope();

    private Oracle O => world.Oracle;

    private T Svc<T>() where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    public void Dispose() => scope.Dispose();

    private static readonly string[] Hoods = [.. SyntheticFeed.Blocks.Select(b => b.Neighborhood).Distinct()];

    private static readonly int[] Wards = [.. SyntheticFeed.Blocks.Select(b => b.Ward).Distinct().Order()];

    private static Func<FeedRecord, bool> Scope(string? hood, int? ward) =>
        r => (hood is null || Oracle.InHood(hood)(r)) && (ward is null || r.Ward == ward);

    public static IEnumerable<object?[]> CrimeScopes()
    {
        yield return [null, null, null];
        foreach (var t in SyntheticFeed.Templates)
        {
            yield return [t.Group, null, null];
        }

        foreach (var h in Hoods)
        {
            yield return [null, h, null];
        }

        foreach (var w in Wards)
        {
            yield return [null, null, w];
        }

        yield return ["theft-from-vehicle", "Whittier", null];
        yield return ["vandalism", null, 12];
        yield return ["shots-fired", "Longfellow", null];
    }

    private double? ExpectedRate(int count, int? residents, int minimum) => residents is { } r ? Oracle.Rate(count, r, minimum) : null;

    // ---------------------------------------------------------------- /crime

    [Fact]
    public async Task TheSiteKnowsExactlyTheGroupsTheFeedProduces()
    {
        var data = await Svc<ICrimeStatsService>().GetCrimeAsync(new CrimeFilter(null, null, null));
        var siteKeys = data.Groups.Concat(data.Metrics).Select(g => g.Key).Order().ToList();

        Assert.Equal(SyntheticFeed.Templates.Select(t => t.Group).Order(), siteKeys);
        Assert.Equal(SyntheticFeed.NonCrimeGroups.Order(), data.Metrics.Select(m => m.Key).Order());
    }

    [Theory]
    [MemberData(nameof(CrimeScopes))]
    public async Task CrimeStats_MatchTheOracle(string? group, string? hood, int? ward)
    {
        var data = await Svc<ICrimeStatsService>().GetCrimeAsync(new CrimeFilter(group, hood, ward));
        var pop = Svc<IPopulationService>();
        var scope = Scope(hood, ward);
        Func<FeedRecord, bool> groupPred = group is null ? Oracle.IsCrime : Oracle.Group(group);

        Assert.Equal(O.Years, data.Years);
        Assert.Equal(O.Through, data.Through);
        Assert.Equal(O.Through.Year, data.CurrentYear);
        Assert.Equal(O.SamePeriod(Oracle.And(groupPred, scope)), data.SamePeriod);
        Assert.Equal(O.Monthly(Oracle.And(groupPred, scope)), data.Monthly);
        Assert.Equal(O.HourWeekday(Oracle.And(groupPred, scope)), data.HourWeekday);
        Assert.Equal(O.HourWeekday(Oracle.And(groupPred, scope)).Sum(d => d.Sum()), data.HourWeekdayTotal);

        var residents = hood is not null ? pop.ForNeighborhood(hood) ?? 0 : ward is not null ? pop.ForWard(ward.Value) ?? 0 : pop.CityTotal;
        var minimum = hood is not null ? pop.MinimumForRate : 1;
        Assert.Equal(residents, data.Population);
        Assert.Equal(hood ?? (ward is not null ? $"Ward {ward}" : "Minneapolis"), data.ScopeLabel);
        Assert.Equal(Oracle.Rate(data.SamePeriod[^1], residents, minimum), data.RatePerThousand);

        // Every group's own row, within the neighborhood/ward scope (the group filter does not narrow these).
        foreach (var row in data.Groups.Concat(data.Metrics))
        {
            var counts = O.SamePeriod(Oracle.And(Oracle.Group(row.Key), scope));
            Assert.Equal(counts, row.Counts);
            Assert.Equal(Oracle.PercentChange(counts[^1], counts[^2]), row.ChangeVsPrior);
            Assert.Equal(Oracle.PercentChange(counts[^1], counts[0]), row.ChangeVsBase);
            Assert.Equal(Oracle.Rate(counts[^1], residents, minimum), row.RatePerThousand);
        }

        // The group table plus nothing else adds up to the all-crimes figure (non-crime rows excluded).
        var allInScope = O.SamePeriod(Oracle.And(Oracle.IsCrime, scope));
        Assert.Equal(allInScope[^1], data.Groups.Sum(g => g.Counts[^1]));
        Assert.Equal(allInScope[0], data.Groups.Sum(g => g.Counts[0]));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("theft-from-vehicle", false)]
    [InlineData("agg-assault", true)]
    [InlineData(null, true)]
    public async Task CrimeStats_AreaRankingsMatchTheOracle(string? group, bool byRate)
    {
        var data = await Svc<ICrimeStatsService>().GetCrimeAsync(new CrimeFilter(group, null, null), byRate);
        var pop = Svc<IPopulationService>();
        Func<FeedRecord, bool> pred = group is null ? Oracle.IsCrime : Oracle.Group(group);

        List<(string Name, int[] Counts, double? Rate)> Expected(Func<FeedRecord, string?> area, Func<string, int?> residents, int minimum) =>
            O.ByArea(pred, area)
                .Select(kv => (Name: kv.Key, Counts: kv.Value, Rate: residents(kv.Key) is { } r ? Oracle.Rate(kv.Value[^1], r, minimum) : null))
                .Where(a => !byRate || a.Rate is not null)
                .OrderByDescending(a => byRate ? a.Rate : a.Counts[^1])
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

        var hoods = Expected(Oracle.HoodOf, n => pop.ForNeighborhood(n), pop.MinimumForRate).Take(10).ToList();
        Assert.Equal(hoods.Select(h => h.Name), data.Neighborhoods.Select(h => h.Name));
        for (var i = 0; i < hoods.Count; i++)
        {
            Assert.Equal(hoods[i].Counts, data.Neighborhoods[i].Counts);
            Assert.Equal(hoods[i].Rate, data.Neighborhoods[i].RatePerThousand);
            Assert.Equal(Oracle.PercentChange(hoods[i].Counts[^1], hoods[i].Counts[^2]), data.Neighborhoods[i].ChangeVsPrior);
            Assert.Equal($"/neighborhoods/{AreaSlug.For(hoods[i].Name)}", data.Neighborhoods[i].Url);
        }

        var wards = Expected(Oracle.WardOf, n => pop.ForWard(int.Parse(n.Split(' ')[1])), 1);
        Assert.Equal(wards.Select(w => w.Name), data.Wards.Select(w => w.Name));
        for (var i = 0; i < wards.Count; i++)
        {
            Assert.Equal(wards[i].Counts, data.Wards[i].Counts);
            Assert.Equal(wards[i].Rate, data.Wards[i].RatePerThousand);
        }
    }

    [Fact]
    public async Task CrimeStats_NonCrimeRowsNeverReachAnyCrimeTotal()
    {
        var data = await Svc<ICrimeStatsService>().GetCrimeAsync(new CrimeFilter(null, null, null));
        var nonCrimeThisYear = O.Visible.Where(r => !r.ExpectedIsCrime && O.InSamePeriod(r.CentralDate) && r.CentralDate.Year == O.Through.Year).Sum(r => r.Count);

        Assert.True(nonCrimeThisYear > 0);
        Assert.Equal(O.Visible.Where(r => r.ExpectedIsCrime && O.InSamePeriod(r.CentralDate) && r.CentralDate.Year == O.Through.Year).Sum(r => r.Count), data.SamePeriod[^1]);
        Assert.Equal(nonCrimeThisYear, data.Metrics.Sum(m => m.Counts[^1]));
    }

    [Fact]
    public async Task CrimeStats_RowsInTheLagWindowAndBeforeTheFirstYearAreExcluded()
    {
        var data = await Svc<ICrimeStatsService>().GetCrimeAsync(new CrimeFilter(null, null, null));
        var held = O.Stored.Where(r => r.CentralDate > O.Through && r.ExpectedIsCrime).Sum(r => r.Count);
        var early = O.Stored.Where(r => r.CentralDate.Year < Oracle.FirstYear && r.ExpectedIsCrime).Sum(r => r.Count);

        Assert.True(held > 0 && early > 0, "The fake feed must include rows in the lag window and before 2019.");
        // Whole-year totals by month: nothing after the through-date, and no 2018.
        var monthlyTotal = data.Monthly.Sum(y => y.Sum(m => m ?? 0));
        Assert.Equal(O.Visible.Where(r => r.ExpectedIsCrime).Sum(r => r.Count), monthlyTotal);
        Assert.Equal(Oracle.FirstYear, data.Years[0]);
    }

    [Fact]
    public async Task Compare_MatchesTheOracleForCityWardsAndNeighborhoods()
    {
        var pop = Svc<IPopulationService>();
        var keys = new[] { "minneapolis", "ward-10", AreaSlug.For("Longfellow"), AreaSlug.For("Downtown West") };
        foreach (var group in new string?[] { null, "burglary", "theft-from-vehicle" })
        {
            var data = await Svc<ICrimeStatsService>().GetCompareAsync(keys, group);
            Func<FeedRecord, bool> pred = group is null ? Oracle.IsCrime : Oracle.Group(group);

            Assert.Equal(4, data.Series.Count);
            Assert.Equal(O.SamePeriod(pred), data.Series[0].Counts);
            Assert.Equal(O.SamePeriod(Oracle.And(pred, r => r.Ward == 10)), data.Series[1].Counts);
            Assert.Equal(O.SamePeriod(Oracle.And(pred, Oracle.InHood("Longfellow"))), data.Series[2].Counts);
            Assert.Equal(O.SamePeriod(Oracle.And(pred, Oracle.InHood("Downtown West"))), data.Series[3].Counts);
            Assert.Equal(O.SamePeriod(pred).Select(c => (double?)Oracle.Rate(c, pop.CityTotal)), data.Series[0].Rates);
        }
    }

    // ---------------------------------------------------------------- /trends (car)

    public static IEnumerable<object?[]> TrendScopes()
    {
        yield return [null, null, null];
        foreach (var category in new[] { "theft-from-vehicle", "vehicle-theft", "parts-theft", "property-damage" })
        {
            yield return [category, null, null];
        }

        yield return [null, "Whittier", null];
        yield return [null, null, 12];
        yield return ["parts-theft", "Phillips West", null];
    }

    private static string GroupOfCategory(string category) => category == "property-damage" ? "vandalism" : category;

    [Theory]
    [MemberData(nameof(TrendScopes))]
    public async Task Trends_MatchTheOracle(string? category, string? hood, int? ward)
    {
        var filter = new TrendFilter(TrendsService.ParseCategory(category), hood, ward);
        var data = await Svc<ITrendsService>().GetTrendsAsync(filter);
        Func<FeedRecord, bool> carPred = category is null ? Oracle.IsCar : Oracle.Group(GroupOfCategory(category));
        var scoped = Oracle.And(carPred, Scope(hood, ward));

        Assert.Equal(O.Years, data.Years);
        Assert.Equal(O.Through, data.Through);
        Assert.Equal(O.SamePeriod(scoped), data.SamePeriod);
        Assert.Equal(O.Monthly(scoped), data.Monthly);

        var hoods = O.ByArea(carPred, Oracle.HoodOf).OrderByDescending(kv => kv.Value[^1]).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Take(10).ToList();
        Assert.Equal(hoods.Select(h => h.Key), data.Neighborhoods.Select(h => h.Name));
        Assert.Equal(hoods.Select(h => h.Value), data.Neighborhoods.Select(h => h.Counts));

        var wards = O.ByArea(carPred, Oracle.WardOf).OrderByDescending(kv => kv.Value[^1]).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Take(10).ToList();
        Assert.Equal(wards.Select(w => w.Key), data.Wards.Select(w => w.Name));
        Assert.Equal(wards.Select(w => w.Value), data.Wards.Select(w => w.Counts));
    }

    // ---------------------------------------------------------------- map

    private (DateOnly From, DateOnly To) Range(string range) => range switch
    {
        "all" => (new DateOnly(2019, 1, 1), O.Through),
        "ytd" => (new DateOnly(O.Through.Year, 1, 1), O.Through),
        _ => (O.Through.AddDays(-(int.Parse(range) - 1)), O.Through)
    };

    private static Func<FeedRecord, bool> MapGroup(string group) => group switch
    {
        "" => Oracle.IsCrime,
        "car" => Oracle.IsCar,
        _ => Oracle.Group(group)
    };

    public static IEnumerable<object[]> MapCases()
    {
        foreach (var group in new[] { "", "car", "theft-from-vehicle", "simple-assault", "shots-fired" })
        {
            foreach (var range in new[] { "7", "30", "90", "365", "ytd", "all" })
            {
                yield return [group, range];
            }
        }
    }

    [Theory]
    [MemberData(nameof(MapCases))]
    public async Task Map_BlocksAndSummaryMatchTheOracle(string group, string range)
    {
        var (from, to) = Range(range);
        var pred = MapGroup(group);
        var map = Svc<MapDataService>();
        var inRange = O.Visible.Where(r => r.CentralDate >= from && r.CentralDate <= to && pred(r) && r.Address != "").ToList();

        var blocks = await map.GetBlocksAsync(new MapFilter(group, from, to));
        Assert.Equal(inRange.Sum(r => r.Count), blocks.Total);
        Assert.Equal(blocks.Total, blocks.Blocks.Sum(b => b.Count));

        var expectedBlocks = inRange.GroupBy(r => r.Address).ToDictionary(g => g.Key, g => g.Sum(r => r.Count));
        var actualBlocks = blocks.Blocks.GroupBy(b => (Math.Round(b.Lat, 4), Math.Round(b.Lng, 4))).ToDictionary(g => g.Key, g => g.Sum(b => b.Count));
        var expectedByPoint = inRange.GroupBy(r => SyntheticFeed.Blocks.First(b => b.Address == r.Address)).ToDictionary(g => (Math.Round(g.Key.Lat, 4), Math.Round(g.Key.Lng, 4)), g => g.Sum(r => r.Count));
        Assert.Equal(expectedBlocks.Values.Order(), blocks.Blocks.Select(b => b.Count).Order());
        Assert.Equal(expectedByPoint.OrderBy(k => k.Key), actualBlocks.OrderBy(k => k.Key));

        // The block's top-types string must add up to no more than its count, and name only matching groups.
        foreach (var block in blocks.Blocks)
        {
            var parts = block.Top.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split(':')).ToList();
            Assert.All(parts, p => Assert.True(inRange.Any(r => r.ExpectedGroup == p[0])));
            Assert.True(parts.Sum(p => int.Parse(p[1])) <= block.Count);
        }

        var summary = await map.GetSummaryAsync(new MapFilter(group, from, to));
        var all = O.Visible.Where(r => r.CentralDate >= from && r.CentralDate <= to && pred(r)).ToList();
        Assert.Equal(all.Sum(r => r.Count), summary.Total);

        var length = to.DayNumber - from.DayNumber + 1;
        var priorTo = from.AddDays(-1);
        var priorFrom = priorTo.AddDays(-(length - 1));
        var hasPrior = priorFrom >= new DateOnly(2019, 1, 1);
        var prior = hasPrior ? O.Sum(pred, priorFrom, priorTo) : 0;
        Assert.Equal(prior, summary.PriorTotal);
        Assert.Equal(hasPrior && prior > 0 ? Oracle.PercentChange(summary.Total, prior) : null, summary.ChangeVsPrior);

        var hoodCounts = all.Where(r => r.Neighborhood is not null).GroupBy(r => r.Neighborhood!).ToDictionary(g => g.Key, g => g.Sum(r => r.Count));
        Assert.Equal(hoodCounts.OrderByDescending(h => h.Value).ThenBy(h => h.Key, StringComparer.OrdinalIgnoreCase).Take(6).Select(h => (h.Key, h.Value)),
            summary.Neighborhoods.Select(h => (h.Name, h.Count)));
        var wardCounts = all.Where(r => r.Ward is > 0).GroupBy(r => r.Ward!.Value).ToDictionary(g => g.Key, g => g.Sum(r => r.Count));
        Assert.Equal(wardCounts.OrderByDescending(w => w.Value).ThenBy(w => w.Key).Take(6).Select(w => ($"Ward {w.Key}", w.Value)),
            summary.Wards.Select(w => (w.Name, w.Count)));

        int Bucket(int h) => h switch { < 6 => 0, < 12 => 1, < 17 => 2, < 22 => 3, _ => 0 };
        var buckets = new int[4];
        foreach (var r in all)
        {
            buckets[Bucket(r.CentralHour)] += r.Count;
        }

        Assert.Equal(buckets, summary.TimeOfDay.Select(t => t.Count));
        Assert.Equal(summary.Total, summary.TimeOfDay.Sum(t => t.Count));

        var typeCounts = all.GroupBy(r => r.ExpectedGroup).Select(g => g.Sum(r => r.Count)).OrderByDescending(c => c).Take(8);
        Assert.Equal(typeCounts, summary.Types.Select(t => t.Count));
    }

    // ---------------------------------------------------------------- homepage figures

    [Fact]
    public async Task HomepageStats_ThirtyDayCountAndChangeMatchTheOracle()
    {
        var stats = await Svc<IReportStatsService>().GetStatsAsync(null, null);
        var now = O.Sum(Oracle.IsCar, O.Through.AddDays(-29), O.Through);
        var before = O.Sum(Oracle.IsCar, O.Through.AddDays(-59), O.Through.AddDays(-30));

        Assert.Equal(now, stats.Count);
        Assert.Equal(before, stats.PriorCount);
        Assert.Equal(Oracle.PercentChange(now, before), stats.PercentChange);
    }

    [Fact]
    public async Task HomepageStats_MonthlyBarsAreTheLastSixCompleteMonths()
    {
        var months = await Svc<IReportStatsService>().GetMonthlyTrendAsync(6);
        var lastMonth = O.Through.AddDays(1).Day == 1 ? new DateOnly(O.Through.Year, O.Through.Month, 1) : new DateOnly(O.Through.Year, O.Through.Month, 1).AddMonths(-1);

        Assert.Equal(6, months.Count);
        for (var i = 0; i < 6; i++)
        {
            var start = lastMonth.AddMonths(i - 5);
            var end = start.AddMonths(1).AddDays(-1);
            Assert.Equal(start.ToString("MMM yyyy"), months[i].MonthLabel);
            Assert.Equal(O.Sum(Oracle.IsCar, start, end), months[i].Count);
        }
    }

    [Fact]
    public async Task HomepageStats_CategoryCountsAreTheFourCarGroupsYearToDate()
    {
        var yearStart = new DateOnly(O.Through.Year, 1, 1);
        var counts = (await Svc<IReportStatsService>().GetCategoryCountsAsync(yearStart, null)).ToDictionary(c => c.Category, c => c.Count);

        Assert.Equal(O.Sum(Oracle.Group("theft-from-vehicle"), yearStart, O.Through), counts[nameof(IncidentType.Unknown)]);
        Assert.Equal(O.Sum(Oracle.Group("vehicle-theft"), yearStart, O.Through), counts[nameof(IncidentType.VehicleStolen)]);
        Assert.Equal(O.Sum(Oracle.Group("parts-theft"), yearStart, O.Through), counts[nameof(IncidentType.PartsTheft)]);
        Assert.Equal(O.Sum(Oracle.Group("vandalism"), yearStart, O.Through), counts[nameof(IncidentType.PropertyDamage)]);
        Assert.Equal(4, counts.Count);
    }

    [Theory]
    [InlineData(IncidentType.Unknown, "theft-from-vehicle")]
    [InlineData(IncidentType.VehicleStolen, "vehicle-theft")]
    [InlineData(IncidentType.PartsTheft, "parts-theft")]
    [InlineData(IncidentType.PropertyDamage, "vandalism")]
    public async Task HomepageStats_YearlyCountsAndProjectionMatchTheOracle(IncidentType type, string group)
    {
        var yearly = await Svc<IReportStatsService>().GetYearlyCountsAsync(type, 2019);
        var pred = Oracle.Group(group);

        // Full calendar years for finished years, the year so far for the current one.
        var full = O.Years.Select(y => O.Sum(pred, new DateOnly(y, 1, 1), y == O.Through.Year ? O.Through : new DateOnly(y, 12, 31))).ToArray();
        Assert.Equal(O.Years, yearly.Select(y => y.Year));
        Assert.Equal(full, yearly.Select(y => y.Count));

        // Projection: this year's count divided by the share of a typical year done by this date (pooled over earlier years).
        var priorYtd = 0;
        var priorFull = 0;
        foreach (var y in O.Years.Where(y => y < O.Through.Year))
        {
            var cutoff = new DateOnly(y, O.Through.Month, Math.Min(O.Through.Day, DateTime.DaysInMonth(y, O.Through.Month)));
            priorYtd += O.Sum(pred, new DateOnly(y, 1, 1), cutoff);
            priorFull += O.Sum(pred, new DateOnly(y, 1, 1), new DateOnly(y, 12, 31));
        }

        var expectedProjection = (int)Math.Round(full[^1] / (priorYtd / (double)priorFull));
        Assert.Equal(expectedProjection, yearly[^1].ProjectedCount);
        Assert.All(yearly.Take(yearly.Count - 1), y => Assert.Null(y.ProjectedCount));
        Assert.Equal(O.Through, yearly[^1].AsOf);
        // A projection must never read lower than what has already happened.
        Assert.True(yearly[^1].ProjectedCount >= yearly[^1].Count);
    }

    // ---------------------------------------------------------------- resident reports

    [Fact]
    public async Task ResidentReports_AreCountedApartAndNeverAddedToMpdFigures()
    {
        var stats = Svc<IReportStatsService>();
        var carNow = O.Sum(Oracle.IsCar, O.Through.AddDays(-29), O.Through);

        Assert.Equal(carNow, (await stats.GetStatsAsync(null, null)).Count);
        // Nine visible resident reports sit inside the same 30 days. They are reported on their own.
        Assert.Equal(9, await stats.GetResidentReportCountAsync(O.Through.AddDays(-29), null));
        var resident = (await stats.GetResidentCategoryCountsAsync(null, null)).ToDictionary(c => c.Category, c => c.Count);
        Assert.Equal(7, resident[nameof(IncidentType.WindowSmashed)]);
        Assert.Equal(2, resident[nameof(IncidentType.Rifled)]);
        Assert.False(resident.ContainsKey(nameof(IncidentType.Unknown)), "An imported MPD row is not a resident report.");

        var mpdOnly = (await stats.GetCategoryCountsAsync(new DateOnly(O.Through.Year, 1, 1), null)).Select(c => c.Category);
        Assert.DoesNotContain(nameof(IncidentType.WindowSmashed), mpdOnly);
        Assert.DoesNotContain(nameof(IncidentType.Rifled), mpdOnly);
    }

    [Fact]
    public async Task ReportingGap_UsesOnlyVisibleResidentReportsThatAnsweredTheQuestion()
    {
        var gap = await Svc<IReportStatsService>().GetPoliceReportingGapAsync();

        // 7 answered (3 yes, 4 no). Pending, removed, flagged, imported and unanswered rows are out.
        Assert.Equal(7, gap.RespondedCount);
        Assert.Equal(Math.Round(4 / 7.0 * 100, 1), gap.PercentUnreported);
    }
}
