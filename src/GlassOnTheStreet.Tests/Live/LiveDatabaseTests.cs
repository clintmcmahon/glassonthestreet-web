using GlassOnTheStreet.Web.Models;
using GlassOnTheStreet.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace GlassOnTheStreet.Tests.Live;

/// <summary>Is what's stored sound? Rules any row in the real database must satisfy, whatever the feed sent.</summary>
[Trait("Category", "Live")]
public class LiveDatabaseTests
{
    private static string Sample(IEnumerable<string> items, int take = 8) =>
        string.Join("\n  ", items.Take(take));

    [LiveFact]
    public void TheDatabaseHoldsTheFeedAndNoRowTwice()
    {
        var rows = LiveWorld.Incidents;

        Assert.True(rows.Count > 1000, $"Only {rows.Count} stored rows.");
        var dupes = rows.GroupBy(r => r.ExternalKey).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dupes.Count == 0, $"{dupes.Count} keys stored more than once:\n  {Sample(dupes)}");
    }

    [LiveFact]
    public void EveryRowIsInAGroupTheSiteKnowsAndTheCrimeFlagMatchesIt()
    {
        var known = CrimeGroups.All.ToDictionary(g => g.Key);
        var unknown = LiveWorld.Incidents.Where(r => !known.ContainsKey(r.GroupKey)).Select(r => r.GroupKey).Distinct().ToList();
        Assert.True(unknown.Count == 0, $"Unknown group keys stored: {string.Join(", ", unknown)}");

        var wrongFlag = LiveWorld.Incidents.Where(r => r.IsCrime != known[r.GroupKey].IsCrime)
            .GroupBy(r => (r.GroupKey, r.IsCrime)).Select(g => $"{g.Key.GroupKey} stored IsCrime={g.Key.IsCrime} ({g.Count()} rows)").ToList();
        Assert.True(wrongFlag.Count == 0, $"IsCrime disagrees with the group:\n  {Sample(wrongFlag)}");
    }

    [LiveFact]
    public void StoredGroupsStillMatchTheCurrentClassificationRules()
    {
        // Catches rows imported under an older rule set (a stale GroupKey would put an offense in the wrong chart).
        var mismatches = LiveWorld.Incidents
            .Where(r => r.OffenseCategory is not null)
            .Select(r => (Row: r, Now: CrimeGroups.Classify(r.OffenseCategory, r.Offense, null).Key))
            .Where(x => x.Now != x.Row.GroupKey)
            .GroupBy(x => (x.Row.OffenseCategory, x.Row.Offense, Stored: x.Row.GroupKey, x.Now))
            .Select(g => $"{g.Key.OffenseCategory} / {g.Key.Offense}: stored '{g.Key.Stored}', rules now say '{g.Key.Now}' ({g.Count()} rows)")
            .ToList();

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} offense types are stored under a group the current rules would not choose:\n  {Sample(mismatches, 15)}");
    }

    [LiveFact]
    public void OtherHoldsNothingThatHasItsOwnGroup()
    {
        // The catch-all must contain only offenses we really have no group for. Lists what is in it so a miss is visible.
        var other = LiveWorld.Incidents.Where(r => r.GroupKey == "other").GroupBy(r => $"{r.OffenseCategory} / {r.Offense}")
            .Select(g => (Name: g.Key, Count: g.Sum(r => (int)r.CrimeCount))).OrderByDescending(x => x.Count).ToList();
        var wronglyThere = other.Where(o => o.Name.Contains("Theft From Motor Vehicle") || o.Name.Contains("Motor Vehicle Theft")
            || o.Name.Contains("Vandalism") || o.Name.Contains("Aggravated Assault") || o.Name.Contains("Burglary")).ToList();

        Assert.True(wronglyThere.Count == 0, $"Offenses with their own group are sitting in 'other':\n  {Sample(wronglyThere.Select(w => $"{w.Name}: {w.Count}"))}");
    }

    [LiveFact]
    public void DatesHoursAndCountsAreInRange()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);
        var rows = LiveWorld.Incidents;

        Assert.True(rows.All(r => r.OccurredHour <= 23), "An hour outside 0 to 23.");
        Assert.True(rows.All(r => r.CrimeCount >= 1), "A CrimeCount below 1.");
        var future = rows.Where(r => r.OccurredDate > today).ToList();
        Assert.True(future.Count == 0, $"{future.Count} rows dated in the future, e.g. {future.FirstOrDefault()?.OccurredDate}.");
        var before = rows.Count(r => r.OccurredDate.Year < 2019);
        // Stray pre-2019 rows are tolerated (the site ignores them) but should stay a handful.
        Assert.True(before < 500, $"{before} rows dated before 2019.");

        // Mass-victim cases are real (20 to 28 victims in one shooting). Anything past 100 is a data error.
        var heavy = rows.Where(r => r.CrimeCount > 100).Select(r => $"{r.ExternalKey}: CrimeCount {r.CrimeCount}").ToList();
        Assert.True(heavy.Count == 0, $"Rows claiming more than 100 offenses (check the feed):\n  {Sample(heavy)}");
    }

    [LiveFact]
    public void LocationsAreBlockMidpointsInsideMinneapolis()
    {
        var zero = LiveWorld.Incidents.Count(r => r.Lat == 0 && r.Lng == 0);
        Assert.True(zero == 0, $"{zero} rows are stored at 0,0. MPD sends that for a record it could not place; it must be stored as no location (the one-time cleanup task does this).");
        var placeholder = LiveWorld.Incidents.Count(r => r.Address == "No Address");
        Assert.True(placeholder == 0, $"{placeholder} rows carry the address 'No Address'. It should be empty.");

        var located = LiveWorld.Incidents.Where(r => r.Lat is not null && r.Lng is not null).ToList();
        var outside = located.Where(r => r.Lat is < 44.85m or > 45.07m || r.Lng is < -93.40m or > -93.15m)
            .Select(r => $"{r.ExternalKey} at {r.Lat},{r.Lng} ({r.Address})").ToList();
        Assert.True(outside.Count == 0, $"{outside.Count} stored points fall outside the Minneapolis area:\n  {Sample(outside)}");

        var inScope = LiveWorld.Incidents.Where(r => r.OccurredDate.Year >= 2019).ToList();
        var unlocated = inScope.Count(r => r.Lat is null || r.Lng is null);
        // These rows count in every total but can't be drawn, so the map's pins trail the page totals by this much.
        Assert.True(unlocated / (double)inScope.Count < 0.02, $"{unlocated / (double)inScope.Count:P1} of rows ({unlocated}) have no location.");
    }

    [LiveFact]
    public void WardsAndNeighborhoodsAreValid()
    {
        var rows = LiveWorld.Incidents.Where(r => r.OccurredDate.Year >= 2019).ToList();
        var badWards = rows.Where(r => r.Ward is { } w && (w < 1 || w > 13)).Select(r => r.Ward!.Value).Distinct().ToList();
        Assert.True(badWards.Count == 0, $"Ward values outside 1 to 13: {string.Join(", ", badWards)}");

        var names = rows.Where(r => r.Neighborhood is not null).Select(r => r.Neighborhood!).Distinct().ToList();
        Assert.InRange(names.Count, 70, 100);
        // The same place spelled two ways would split its counts.
        var folded = names.GroupBy(n => AreaSlug.For(n)).Where(g => g.Count() > 1).Select(g => string.Join(" | ", g)).ToList();
        Assert.True(folded.Count == 0, $"Neighborhood names that collapse to one slug:\n  {Sample(folded)}");
        var trimmed = names.Where(n => n != n.Trim()).ToList();
        Assert.True(trimmed.Count == 0, $"Neighborhood names with stray spaces: {string.Join(", ", trimmed)}");
    }

    [LiveFact]
    public void EveryMonthSinceTheFirstYearHasDataAndNoneCollapses()
    {
        var oracle = LiveWorld.Oracle;
        var monthly = oracle.Monthly(GlassOnTheStreet.Tests.DataAccuracy.Oracle.IsCrime);
        var problems = new List<string>();
        for (var y = 0; y < oracle.Years.Length; y++)
        {
            for (var m = 0; m < 12; m++)
            {
                if (monthly[y][m] is not { } total)
                {
                    continue;
                }

                if (total == 0)
                {
                    problems.Add($"{oracle.Years[y]}-{m + 1:00}: no offenses at all (an import gap?)");
                }
            }
        }

        // A finished month far below its neighbors is the signature of a half-imported month.
        var series = Enumerable.Range(0, oracle.Years.Length * 12).Select(i => (Year: oracle.Years[i / 12], Month: i % 12 + 1, Total: monthly[i / 12][i % 12])).Where(x => x.Total is not null).ToList();
        for (var i = 1; i < series.Count - 1; i++)
        {
            var neighborAverage = (series[i - 1].Total!.Value + series[i + 1].Total!.Value) / 2.0;
            if (series[i].Year >= 2020 && series[i].Total < neighborAverage * 0.55)
            {
                problems.Add($"{series[i].Year}-{series[i].Month:00}: {series[i].Total} offenses vs neighbors averaging {neighborAverage:N0}");
            }
        }

        Assert.True(problems.Count == 0, $"Suspicious months:\n  {Sample(problems, 20)}");
    }

    [LiveFact]
    public void ResidentReportsAreSnappedToBlocksAndNeverLookLikeMpdRows()
    {
        using var db = LiveWorld.NewContext();
        var resident = db.Reports.AsNoTracking().Where(r => r.SourceType == SourceType.UserReport).ToList();
        var anchors = LiveWorld.Incidents.Where(i => i.Lat is not null).Select(i => (i.Lat, i.Lng)).ToHashSet();

        // A resident report whose street has no MPD anchor falls back to a coarse grid point; those are expected to be rare.
        var unsnapped = resident.Where(r => !anchors.Contains((r.DisplayLat, r.DisplayLng))).Select(r => $"report {r.Id} at {r.DisplayLat},{r.DisplayLng} ({r.CrossStreets})").ToList();
        Assert.True(unsnapped.Count <= Math.Max(1, resident.Count / 10), $"{unsnapped.Count} of {resident.Count} resident reports are not on an MPD block midpoint:\n  {Sample(unsnapped)}");
        Assert.True(resident.All(r => r.ExternalCaseNumber is null), "A resident report carries an MPD case number.");
        Assert.True(resident.All(r => r.DisplayLat is >= 44.85m and <= 45.07m && r.DisplayLng is >= -93.40m and <= -93.15m), "A resident report is outside Minneapolis.");
    }

    [LiveFact]
    public void CarPinsInTheReportsTableAgreeWithTheFullFeed()
    {
        using var db = LiveWorld.NewContext();
        var pins = db.Reports.AsNoTracking().Where(r => r.SourceType == SourceType.OfficialImport && r.Status == ReportStatus.Active)
            .Select(r => new { r.ExternalCaseNumber, r.ReportedDate, r.IncidentType }).ToList();
        string[] car = ["theft-from-vehicle", "vehicle-theft", "parts-theft", "vandalism"];

        var byYearPins = pins.GroupBy(p => p.ReportedDate.Year).ToDictionary(g => g.Key, g => g.Select(p => p.ExternalCaseNumber).Distinct().Count());
        // The pins exist only for records with a real location inside the service area (see GeofenceService).
        var byYearFeed = LiveWorld.Incidents.Where(i => car.Contains(i.GroupKey) && i.OccurredDate.Year >= 2019
                && i.Lat is not null && i.Lat != 0 && i.Lat >= 44.85m && i.Lat <= 45.07m && i.Lng >= -93.40m && i.Lng <= -93.15m)
            .GroupBy(i => i.OccurredDate.Year).ToDictionary(g => g.Key, g => g.Select(i => i.CaseNumber).Distinct().Count());

        var diffs = byYearFeed.Select(kv => (Year: kv.Key, Feed: kv.Value, Pins: byYearPins.GetValueOrDefault(kv.Key)))
            .Where(d => Math.Abs(d.Feed - d.Pins) > Math.Max(5, d.Feed * 0.005))
            .Select(d => $"{d.Year}: {d.Feed} distinct car cases in the full feed, {d.Pins} in the Reports table").ToList();
        Assert.True(diffs.Count == 0, $"The two importers disagree on car cases:\n  {Sample(diffs)}");
    }
}
