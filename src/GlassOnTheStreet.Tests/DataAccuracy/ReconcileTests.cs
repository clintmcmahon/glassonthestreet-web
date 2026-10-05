using GlassOnTheStreet.Web.Data;
using GlassOnTheStreet.Web.Models;
using GlassOnTheStreet.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GlassOnTheStreet.Tests.DataAccuracy;

/// <summary>
/// MPD posts records weeks or months late and withdraws or revises others. The reconcile pass has to leave the
/// database identical to the feed, whatever happened to the feed after the first import.
/// </summary>
public class ReconcileTests
{
    private static readonly DateOnly First = new(2019, 1, 1);

    private sealed class Rig
    {
        public required GlassOnTheStreetContext Db { get; init; }

        public required MpdIncidentImportService Importer { get; init; }

        public required List<FeedRecord> Feed { get; init; }

        public required SyntheticFeed.FeedHandler Handler { get; init; }

        public Task<ReconcileResult> ReconcileAsync() => Importer.ReconcileMonthsAsync(First, new DateOnly(2026, 9, 1));
    }

    private static async Task<Rig> StartAsync(int count = 2500)
    {
        var feed = SyntheticFeed.Generate(count);
        var db = new GlassOnTheStreetContext(new DbContextOptionsBuilder<GlassOnTheStreetContext>().UseInMemoryDatabase($"reconcile-{Guid.NewGuid():N}").Options);
        var handler = new SyntheticFeed.FeedHandler(feed);
        var importer = new MpdIncidentImportService(new HttpClient(handler) { BaseAddress = new Uri("https://feed.test/") }, db, NullLogger<MpdIncidentImportService>.Instance);
        for (var year = 2019; year <= 2026; year++)
        {
            await importer.ImportRangeAsync(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));
        }

        return new Rig { Db = db, Importer = importer, Feed = feed, Handler = handler };
    }

    private static string Shape(string key, DateOnly date, int hour, string group, int count, string? hood, int? ward) =>
        $"{key} | {date:yyyy-MM-dd} {hour:00} | {group} | {count} | {hood} | {ward}";

    /// <summary>The database and the feed hold the same rows, field for field.</summary>
    private static async Task AssertDatabaseEqualsFeedAsync(Rig rig)
    {
        var expected = new Oracle(rig.Feed).Keyed.Where(k => k.Row.CentralDate.Year >= 2019)
            .Select(k => Shape(k.StoredKey, k.Row.CentralDate, k.Row.CentralHour, k.Row.ExpectedGroup, k.Row.Count, k.Row.Neighborhood, k.Row.Ward)).Order().ToList();
        var actual = (await rig.Db.MpdIncidents.AsNoTracking().ToListAsync())
            .Select(i => Shape(i.ExternalKey, i.OccurredDate, i.OccurredHour, i.GroupKey, i.CrimeCount, i.Neighborhood, i.Ward)).Order().ToList();

        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task AFeedThatHasNotChangedNeedsNoRowsFetched()
    {
        var rig = await StartAsync();
        await AssertDatabaseEqualsFeedAsync(rig);
        var before = rig.Handler.RequestCount;

        var result = await rig.ReconcileAsync();

        Assert.Equal(0, result.MonthsChanged);
        Assert.Equal(0, result.Inserted + result.Updated + result.Deleted);
        // Four small statistics queries per month (total, by offense, by neighborhood, by ward) and no row downloads.
        Assert.Equal(result.MonthsChecked * 4, rig.Handler.RequestCount - before);
    }

    [Fact]
    public async Task RecordsPostedWeeksOrMonthsLateAreAdded()
    {
        var rig = await StartAsync();
        var template = rig.Feed.First(r => r.ExpectedGroup == "theft-from-vehicle" && r.Located);
        // Occurred long ago, appeared in the feed only now. The 30-day additive sync would never ask for these dates.
        rig.Feed.Add(SyntheticFeed.OnDate(template with { Case = "late-1" }, new DateOnly(2022, 3, 9)));
        rig.Feed.Add(SyntheticFeed.OnDate(template with { Case = "late-2", Count = 3 }, new DateOnly(2022, 3, 20)));
        rig.Feed.Add(SyntheticFeed.OnDate(template with { Case = "late-3" }, new DateOnly(2020, 11, 2)));

        var result = await rig.ReconcileAsync();

        Assert.Equal(2, result.MonthsChanged);
        Assert.Equal(3, result.Inserted);
        await AssertDatabaseEqualsFeedAsync(rig);
    }

    [Fact]
    public async Task ASecondRowForTheSameCaseAndOffenseIsKeptNotMistakenForARepeat()
    {
        var rig = await StartAsync();
        var original = rig.Feed.First(r => r.ExpectedGroup == "weapons" && r.CentralDate.Year == 2024);
        rig.Feed.Add(original with { Count = 1 });
        rig.Feed.Add(original with { Count = 1 });

        await rig.ReconcileAsync();

        await AssertDatabaseEqualsFeedAsync(rig);
        var keys = await rig.Db.MpdIncidents.AsNoTracking().Where(i => i.CaseNumber == original.Case && i.GroupKey == "weapons").Select(i => i.ExternalKey).ToListAsync();
        Assert.Contains(keys, k => k.EndsWith("#3"));
    }

    [Fact]
    public async Task RecordsTheCityWithdrewAreRemoved()
    {
        var rig = await StartAsync();
        // Distinct rows in one month (a repeated row has an identical twin, which would muddy the count).
        var gone = rig.Feed.Where(r => r.CentralDate is { Year: 2021, Month: 6 }).GroupBy(r => r.Key).Where(g => g.Count() == 1).Select(g => g.First()).Take(3).ToList();
        Assert.Equal(3, gone.Count);
        foreach (var r in gone)
        {
            rig.Feed.Remove(r);
        }

        var result = await rig.ReconcileAsync();

        Assert.Equal(3, result.Deleted);
        await AssertDatabaseEqualsFeedAsync(rig);
    }

    [Fact]
    public async Task RevisedRecordsTakeTheCurrentValues()
    {
        var rig = await StartAsync();
        var single = rig.Feed.GroupBy(r => r.Key).Where(g => g.Count() == 1).Select(g => g.First()).ToList();
        var a = single.First(r => r.CentralDate is { Year: 2023, Month: 4 } && r.Located);
        var b = single.First(r => r.CentralDate is { Year: 2023, Month: 4 } && r.Located && r != a);
        var c = single.First(r => r.CentralDate is { Year: 2023, Month: 9 } && r.Located);
        rig.Feed[rig.Feed.IndexOf(a)] = a with { Count = a.Count + 4 };                        // more victims recorded
        var elsewhere = SyntheticFeed.Blocks.First(x => x.Neighborhood != b.Neighborhood);
        rig.Feed[rig.Feed.IndexOf(b)] = b with { Neighborhood = elsewhere.Neighborhood, Ward = elsewhere.Ward }; // placed in a different neighborhood
        rig.Feed[rig.Feed.IndexOf(c)] = SyntheticFeed.OnDate(c, new DateOnly(2023, 10, 2));    // moved to the next month

        var result = await rig.ReconcileAsync();

        // The count change and the neighborhood change are edits. The move to another month is a removal from September
        // and an addition to October.
        Assert.Equal(2, result.Updated);
        Assert.Equal(1, result.Inserted);
        Assert.Equal(1, result.Deleted);
        await AssertDatabaseEqualsFeedAsync(rig);
    }

    [Fact]
    public async Task AnOffenseReclassifiedWithinAMonthIsCorrectedEvenThoughTheMonthsTotalsDoNotChange()
    {
        var rig = await StartAsync();
        var single = rig.Feed.GroupBy(r => r.Key).Where(g => g.Count() == 1).Select(g => g.First()).ToList();
        var assault = single.First(r => r.CentralDate is { Year: 2022, Month: 7 } && r.ExpectedGroup == "agg-assault");
        var simple = SyntheticFeed.Templates.First(t => t.Group == "simple-assault");
        // Same case, same day, same count: only the offense changed. Row and offense totals for July are untouched.
        rig.Feed[rig.Feed.IndexOf(assault)] = assault with { Offense = simple.Offense, NibrsCode = simple.Nibrs, ExpectedGroup = "simple-assault" };

        var result = await rig.ReconcileAsync();

        Assert.Equal(1, result.MonthsChanged);
        await AssertDatabaseEqualsFeedAsync(rig);
    }

    [Fact]
    public async Task ARecordPlacedInADifferentNeighborhoodIsCorrectedEvenThoughTheTotalsDoNotChange()
    {
        var rig = await StartAsync();
        var single = rig.Feed.GroupBy(r => r.Key).Where(g => g.Count() == 1).Select(g => g.First()).ToList();
        var row = single.First(r => r.CentralDate is { Year: 2021, Month: 2 } && r.Located);
        var elsewhere = SyntheticFeed.Blocks.First(x => x.Neighborhood != row.Neighborhood);
        rig.Feed[rig.Feed.IndexOf(row)] = row with { Neighborhood = elsewhere.Neighborhood, Ward = elsewhere.Ward };

        var result = await rig.ReconcileAsync();

        Assert.Equal(1, result.MonthsChanged);
        Assert.Equal(1, result.Updated);
        await AssertDatabaseEqualsFeedAsync(rig);
    }

    [Fact]
    public async Task EverythingAtOnceEndsIdenticalToTheFeedAndASecondPassChangesNothing()
    {
        var rig = await StartAsync();
        var template = rig.Feed.First(r => r.ExpectedGroup == "vehicle-theft" && r.Located);
        rig.Feed.Add(SyntheticFeed.OnDate(template with { Case = "late-a" }, new DateOnly(2021, 8, 3)));
        rig.Feed.Add(SyntheticFeed.OnDate(template with { Case = "late-b" }, new DateOnly(2026, 2, 3)));
        foreach (var r in rig.Feed.Where(r => r.CentralDate is { Year: 2020, Month: 10 } && r.ExpectedGroup == "shots-fired").Take(4).ToList())
        {
            rig.Feed.Remove(r);
        }

        var moved = rig.Feed.First(r => r.CentralDate is { Year: 2024, Month: 1 });
        rig.Feed[rig.Feed.IndexOf(moved)] = SyntheticFeed.OnDate(moved, new DateOnly(2023, 12, 30));

        await rig.ReconcileAsync();
        await AssertDatabaseEqualsFeedAsync(rig);

        var again = await rig.ReconcileAsync();
        Assert.Equal(0, again.MonthsChanged);
    }

    [Fact]
    public async Task AMonthThatWouldLoseALargeShareIsLeftAloneAsLikelyABadRead()
    {
        var rig = await StartAsync(6000);
        var month = rig.Feed.Where(r => r.CentralDate is { Year: 2023, Month: 5 }).ToList();
        Assert.True(month.Count > 60);
        var before = await rig.Db.MpdIncidents.CountAsync();
        foreach (var r in month.Take(month.Count * 2 / 3))
        {
            rig.Feed.Remove(r);
        }

        var result = await rig.ReconcileAsync();

        Assert.Equal(0, result.Deleted);
        Assert.True(result.DeletesSkipped > 0);
        Assert.Equal(before, await rig.Db.MpdIncidents.CountAsync());
    }

    [Fact]
    public async Task APageThatFailsChangesNothing()
    {
        var rig = await StartAsync();
        var template = rig.Feed.First(r => r.ExpectedGroup == "vehicle-theft" && r.Located);
        rig.Feed.Add(SyntheticFeed.OnDate(template with { Case = "late-x" }, new DateOnly(2022, 3, 9)));
        rig.Handler.FailAtOffset = _ => true;
        var before = await rig.Db.MpdIncidents.CountAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => rig.ReconcileAsync());

        Assert.Equal(before, await rig.Db.MpdIncidents.CountAsync());
    }

    [Fact]
    public async Task TheDailyAdditiveImportStillWorksAfterAReconcile()
    {
        var rig = await StartAsync();
        var template = rig.Feed.First(r => r.ExpectedGroup == "other" && r.Located);
        rig.Feed.Add(SyntheticFeed.OnDate(template with { Case = "new-1" }, new DateOnly(2026, 9, 10)));
        await rig.ReconcileAsync();
        rig.Feed.Add(SyntheticFeed.OnDate(template with { Case = "new-2" }, new DateOnly(2026, 9, 11)));

        var result = await rig.Importer.ImportRangeAsync(new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 30));

        Assert.Equal(1, result.Imported);
        await AssertDatabaseEqualsFeedAsync(rig);
    }
}
