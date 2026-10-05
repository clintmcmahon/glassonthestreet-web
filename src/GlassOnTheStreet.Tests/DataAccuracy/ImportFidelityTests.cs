using GlassOnTheStreet.Web.Data;
using GlassOnTheStreet.Web.Models;
using GlassOnTheStreet.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GlassOnTheStreet.Tests.DataAccuracy;

/// <summary>Source feed to database: what MPD publishes is what gets stored, once, with the right group, date and place.</summary>
[Collection("world")]
public class ImportFidelityTests(World world)
{
    private async Task<List<MpdIncident>> StoredAsync()
    {
        using var scope = world.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<GlassOnTheStreetContext>().MpdIncidents.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task EveryFeedRowIsStoredExactlyOnce_AndRowsBeforeTheFirstYearAreNot()
    {
        var stored = await StoredAsync();
        var expected = world.Oracle.Keyed.Where(k => k.Row.CentralDate.Year >= Oracle.FirstYear).Select(k => k.StoredKey).ToList();

        Assert.Equal(expected.Count, stored.Count);
        Assert.Equal(stored.Count, stored.Select(s => s.ExternalKey).Distinct().Count());
        Assert.Equal(expected.Order(), stored.Select(s => s.ExternalKey).Order());
        Assert.DoesNotContain(stored, s => s.OccurredDate.Year < Oracle.FirstYear);
    }

    [Fact]
    public async Task ARowTheFeedListsTwiceIsStoredTwiceBecauseTheCitysTotalsCountBoth()
    {
        // Two weapons offenses in one case, two ShotSpotter activations: separate rows in the feed, separate offenses.
        var repeated = world.Oracle.Stored.Where(r => r.CentralDate.Year >= Oracle.FirstYear).GroupBy(r => r.Key).Where(g => g.Count() > 1).ToList();
        Assert.True(repeated.Count > 50, "The fake feed must include repeated rows.");

        var stored = (await StoredAsync()).Select(s => s.ExternalKey).ToHashSet();
        foreach (var g in repeated)
        {
            Assert.Contains(g.Key, stored);
            Assert.Contains($"{g.Key}#2", stored);
        }

        Assert.Equal(0, world.YearResults.Sum(r => r.SkippedDuplicate));
        Assert.Equal(0, world.YearResults.Sum(r => r.SkippedInvalid));
    }

    [Fact]
    public async Task EveryStoredFieldMatchesTheSourceRow()
    {
        var stored = (await StoredAsync()).ToDictionary(s => s.ExternalKey);
        var problems = new List<string>();

        foreach (var (src, storedKey) in world.Oracle.Keyed.Where(k => k.Row.CentralDate.Year >= Oracle.FirstYear))
        {
            var db = stored[storedKey];
            void Check(string field, object? want, object? got)
            {
                if (!Equals(want, got))
                {
                    problems.Add($"{storedKey} {field}: source {want}, stored {got}");
                }
            }

            Check(nameof(db.CaseNumber), src.Case, db.CaseNumber);
            Check(nameof(db.OccurredDate), src.CentralDate, db.OccurredDate);
            Check(nameof(db.OccurredHour), (byte)src.CentralHour, db.OccurredHour);
            Check(nameof(db.GroupKey), src.ExpectedGroup, db.GroupKey);
            Check(nameof(db.IsCrime), src.ExpectedIsCrime, db.IsCrime);
            Check(nameof(db.CrimeCount), (short)src.Count, db.CrimeCount);
            Check(nameof(db.Neighborhood), src.Neighborhood, db.Neighborhood);
            Check(nameof(db.Ward), (byte?)src.Ward, db.Ward);
            Check(nameof(db.Precinct), (byte?)src.Precinct, db.Precinct);
            Check(nameof(db.Offense), src.Offense, db.Offense);
            Check(nameof(db.OffenseCategory), src.Category, db.OffenseCategory);
            Check(nameof(db.CrimeAgainst), src.CrimeAgainst, db.CrimeAgainst);
            Check(nameof(db.Address), src.Address == "" ? null : src.Address, db.Address);

            var block = SyntheticFeed.Blocks.FirstOrDefault(b => b.Address == src.Address);
            if (block is null)
            {
                Check(nameof(db.Lat), null, db.Lat);
            }
            else
            {
                // Web Mercator meters back to degrees must land on the block to within a few feet.
                Assert.NotNull(db.Lat);
                Assert.NotNull(db.Lng);
                if (Math.Abs((double)db.Lat!.Value - block.Lat) > 1e-5 || Math.Abs((double)db.Lng!.Value - block.Lng) > 1e-5)
                {
                    problems.Add($"{storedKey} location: source {block.Lat},{block.Lng}, stored {db.Lat},{db.Lng}");
                }
            }
        }

        Assert.True(problems.Count == 0, $"{problems.Count} field mismatches. First ones:\n{string.Join("\n", problems.Take(15))}");
    }

    [Fact]
    public async Task OffenseCountsPerYearAndGroupAddUpToTheSource()
    {
        var stored = await StoredAsync();
        var fromDb = stored.GroupBy(s => (s.OccurredDate.Year, s.GroupKey)).ToDictionary(g => g.Key, g => g.Sum(s => (int)s.CrimeCount));
        var fromSource = world.Oracle.Stored.Where(r => r.CentralDate.Year >= Oracle.FirstYear)
            .GroupBy(r => (r.CentralDate.Year, r.ExpectedGroup)).ToDictionary(g => g.Key, g => g.Sum(r => r.Count));

        Assert.Equal(fromSource.OrderBy(k => k.Key), fromDb.OrderBy(k => k.Key));
    }

    [Fact]
    public async Task DatesAreCentralTimeNotUtc()
    {
        var stored = (await StoredAsync()).Where(s => s.CaseNumber != "26-MULTI1" && !s.ExternalKey.Contains('#')).ToDictionary(s => s.CaseNumber);

        // 11:30 pm Central on Dec 31 is already Jan 1 in UTC. It belongs to 2020.
        Assert.Equal(new DateOnly(2020, 12, 31), stored["20-EDGE01"].OccurredDate);
        Assert.Equal(23, stored["20-EDGE01"].OccurredHour);
        // 12:30 am Central on Jan 1 is 6:30 am UTC the same day. Still 2021.
        Assert.Equal(new DateOnly(2021, 1, 1), stored["20-EDGE02"].OccurredDate);
        Assert.Equal(0, stored["20-EDGE02"].OccurredHour);
    }

    [Fact]
    public async Task DaylightSavingAndYearBoundaryRowsKeepTheirLocalClockTime()
    {
        var stored = (await StoredAsync()).Where(s => s.CaseNumber != "26-MULTI1" && !s.ExternalKey.Contains('#')).ToDictionary(s => s.CaseNumber);

        Assert.Equal((new DateOnly(2021, 3, 14), (byte)3), (stored["21-EDGE03"].OccurredDate, stored["21-EDGE03"].OccurredHour));
        Assert.Equal((new DateOnly(2021, 11, 7), (byte)0), (stored["21-EDGE04"].OccurredDate, stored["21-EDGE04"].OccurredHour));
        Assert.Equal((new DateOnly(2019, 1, 1), (byte)0), (stored["19-EDGE05"].OccurredDate, stored["19-EDGE05"].OccurredHour));
        Assert.Equal((new DateOnly(2026, 9, 30), (byte)23), (stored["26-EDGE06"].OccurredDate, stored["26-EDGE06"].OccurredHour));
    }

    [Fact]
    public async Task RecordsMpdCouldNotPlaceAreStoredWithNoLocationNotAtZeroZero()
    {
        var stored = await StoredAsync();
        var unplaced = world.Oracle.Keyed.Where(k => !k.Row.Located && k.Row.CentralDate.Year >= Oracle.FirstYear).Select(k => k.StoredKey).ToHashSet();

        Assert.True(unplaced.Count > 100, "The fake feed must include unplaced rows.");
        var rows = stored.Where(s => unplaced.Contains(s.ExternalKey)).ToList();
        Assert.Equal(unplaced.Count, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Null(r.Lat);
            Assert.Null(r.Lng);
            Assert.Null(r.Address);
        });
        Assert.DoesNotContain(stored, s => s.Lat == 0 && s.Lng == 0);
        Assert.DoesNotContain(stored, s => s.Address == "No Address");
    }

    [Fact]
    public async Task OneCaseWithTwoDifferentOffensesKeepsBothRows()
    {
        var multi = (await StoredAsync()).Where(s => s.CaseNumber == "26-MULTI1").Select(s => s.GroupKey).Order().ToList();

        Assert.Equal(["parts-theft", "vandalism"], multi);
    }

    [Fact]
    public async Task NonCrimeRowsAreStoredButFlaggedSoNothingCountsThem()
    {
        var stored = await StoredAsync();

        foreach (var key in SyntheticFeed.NonCrimeGroups)
        {
            var rows = stored.Where(s => s.GroupKey == key).ToList();
            Assert.NotEmpty(rows);
            Assert.All(rows, r => Assert.False(r.IsCrime));
        }

        Assert.All(stored.Where(s => !SyntheticFeed.NonCrimeGroups.Contains(s.GroupKey)), r => Assert.True(r.IsCrime));
    }

    [Fact]
    public async Task ReImportingAYearChangesNothing()
    {
        var before = (await StoredAsync()).Count;
        using var scope = world.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GlassOnTheStreetContext>();
        var importer = new MpdIncidentImportService(
            new HttpClient(new SyntheticFeed.FeedHandler(world.Feed)) { BaseAddress = new Uri("https://feed.test/") },
            db, NullLogger<MpdIncidentImportService>.Instance);

        var result = await importer.ImportRangeAsync(new DateOnly(2024, 1, 1), new DateOnly(2024, 12, 31));

        Assert.Equal(0, result.Imported);
        Assert.True(result.Fetched > 0);
        Assert.Equal(result.Fetched, result.SkippedDuplicate); // everything is already on file, repeated rows included
        Assert.Equal(before, (await StoredAsync()).Count);
    }

    [Fact]
    public async Task APageThatFailsMakesTheImportThrowInsteadOfLookingFinished()
    {
        var options = new DbContextOptionsBuilder<GlassOnTheStreetContext>().UseInMemoryDatabase($"fail-{Guid.NewGuid():N}").Options;
        await using var db = new GlassOnTheStreetContext(options);
        var handler = new SyntheticFeed.FeedHandler(world.Feed) { FailAtOffset = offset => offset >= 1000 };
        var importer = new MpdIncidentImportService(new HttpClient(handler) { BaseAddress = new Uri("https://feed.test/") }, db, NullLogger<MpdIncidentImportService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportRangeAsync(new DateOnly(2019, 1, 1), new DateOnly(2026, 12, 31)));
    }
}
