using GlassOnTheStreet.Web.Data;
using GlassOnTheStreet.Web.Models;
using GlassOnTheStreet.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GlassOnTheStreet.Tests.DataAccuracy;

/// <summary>Rows MPD couldn't place used to be stored at 0,0 and drawn as a pin off the coast of Africa.</summary>
public class UnplacedLocationTests
{
    private static GlassOnTheStreetContext NewDb() =>
        new(new DbContextOptionsBuilder<GlassOnTheStreetContext>().UseInMemoryDatabase($"unplaced-{Guid.NewGuid():N}").Options);

    private static MpdIncident Row(string key, decimal? lat, decimal? lng, string? address, short count = 1) => new()
    {
        ExternalKey = key, CaseNumber = key, OccurredDate = new DateOnly(2026, 6, 1), OccurredHour = 10, GroupKey = "drugs",
        Offense = "Drug/Narcotic Violations", IsCrime = true, CrimeCount = count, Lat = lat, Lng = lng, Address = address,
        Neighborhood = "Whittier", Ward = 10
    };

    // The newest 10 days are held back, so a later record is what makes June count as complete.
    private static MpdIncident Newest()
    {
        var row = Row("newest", 44.95m, -93.27m, "0001XX MAIN ST");
        row.OccurredDate = new DateOnly(2026, 9, 30);
        return row;
    }

    [Fact]
    public async Task TheCacheTreatsZeroZeroAsNoLocationButStillCountsTheOffense()
    {
        await using var db = NewDb();
        db.MpdIncidents.AddRange(
            Row("placed", 44.9552m, -93.2776m, "0026XX NICOLLET AVE"),
            Row("zero-zero", 0m, 0m, "No Address", count: 3),
            Row("null", null, null, null),
            Newest());
        await db.SaveChangesAsync();

        var dataset = await new IncidentDataCache(db, new MemoryCache(new MemoryCacheOptions())).GetAsync();

        Assert.Equal(3, dataset.Rows.Length);
        Assert.Equal(1, dataset.Rows.Count(r => !float.IsNaN(r.Lat)));
        Assert.Equal(5, dataset.Rows.Sum(r => r.Count));
        Assert.DoesNotContain(dataset.Rows, r => r.Lat == 0f);
    }

    [Fact]
    public async Task MapPinsLeaveOutUnplacedRowsAndReportHowManyWereLeftOut()
    {
        await using var db = NewDb();
        db.MpdIncidents.AddRange(
            Row("placed", 44.9552m, -93.2776m, "0026XX NICOLLET AVE", count: 2),
            Row("zero-zero", 0m, 0m, "No Address", count: 3),
            Newest());
        await db.SaveChangesAsync();

        var map = new MapDataService(new IncidentDataCache(db, new MemoryCache(new MemoryCacheOptions())));
        var day = new DateOnly(2026, 6, 1);
        var blocks = await map.GetBlocksAsync(new MapFilter("", day, day));
        var summary = await map.GetSummaryAsync(new MapFilter("", day, day));

        Assert.Equal(2, blocks.Total);
        Assert.Equal(3, blocks.Unlocated);
        Assert.Single(blocks.Blocks);
        Assert.Equal(5, summary.Total);
    }

    [Fact]
    public async Task TheOneTimeCleanupClearsZeroZeroOnlyAndOnlyOnce()
    {
        await using var db = NewDb();
        db.MpdIncidents.AddRange(
            Row("placed", 44.9552m, -93.2776m, "0026XX NICOLLET AVE"),
            Row("zero-zero", 0m, 0m, "No Address"),
            Row("zero-with-address", 0m, 0m, "0010XX 5TH ST"));
        await db.SaveChangesAsync();
        var service = new OfficialDataSyncBackgroundService(null!, NullLogger<OfficialDataSyncBackgroundService>.Instance);

        Assert.Equal(2, await service.ClearUnplacedLocationsOnceAsync(db, default));
        Assert.Equal(0, await service.ClearUnplacedLocationsOnceAsync(db, default));

        var rows = await db.MpdIncidents.AsNoTracking().ToDictionaryAsync(r => r.ExternalKey);
        Assert.Equal(44.9552m, rows["placed"].Lat);
        Assert.Equal("0026XX NICOLLET AVE", rows["placed"].Address);
        Assert.Null(rows["zero-zero"].Lat);
        Assert.Null(rows["zero-zero"].Address);
        Assert.Null(rows["zero-with-address"].Lng);
        Assert.Equal("0010XX 5TH ST", rows["zero-with-address"].Address); // only the "No Address" placeholder is dropped
    }

    [Fact]
    public void TheExpectedStartupTasksIncludeTheCleanupAndVersionedCarImports()
    {
        var keys = OfficialDataSyncBackgroundService.ExpectedTasks(2026).Select(t => t.Key).ToList();

        Assert.Contains(OfficialDataSyncBackgroundService.ClearUnplacedLocationsTaskKey, keys);
        Assert.Contains("CarImport-v2-2019", keys);
        Assert.DoesNotContain("CarImport-2019", keys);
    }
}
