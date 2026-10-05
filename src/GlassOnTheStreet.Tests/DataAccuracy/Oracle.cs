namespace GlassOnTheStreet.Tests.DataAccuracy;

/// <summary>
/// The expected numbers, computed straight from the fake feed with plain loops. It shares no code with
/// the site's services: the rules below are written out from the site's published methodology.
///   - every row the feed lists is stored, including a second row with the same case, NIBRS code and offense
///     (the city's own totals count each); a re-import never stores a row twice
///   - statistics cover 2019-01-01 through the newest stored date minus 10 days (MPD posts late)
///   - an "offense" is the row's Crime_Count, so a row with Crime_Count 2 is two offenses
///   - year comparisons use the same calendar window (Jan 1 through the through-date's month and day)
/// </summary>
public sealed class Oracle
{
    public static readonly string[] CarGroups = ["theft-from-vehicle", "vehicle-theft", "parts-theft", "vandalism"];

    public const int FirstYear = 2019;

    public IReadOnlyList<FeedRecord> Stored { get; }

    /// <summary>Each stored row with the key it is saved under: the plain key, or "#2", "#3" for later rows of the same case, code and offense.</summary>
    public IReadOnlyList<(FeedRecord Row, string StoredKey)> Keyed { get; }

    public IReadOnlyList<FeedRecord> Visible { get; }

    public DateOnly Through { get; }

    public int[] Years { get; }

    public Oracle(IEnumerable<FeedRecord> feed, DateOnly? today = null)
    {
        var all = feed.ToList();
        Stored = all;
        var seen = new Dictionary<string, int>();
        Keyed = all.Select((r, i) => (r, i)).OrderBy(x => x.r.OccurredMs).ThenBy(x => x.i)
            .Select(x => (x.r, StoredKey: seen[x.r.Key] = seen.GetValueOrDefault(x.r.Key) + 1) )
            .Select(x => (x.r, x.StoredKey == 1 ? x.r.Key : $"{x.r.Key}#{x.StoredKey}"))
            .ToList();

        var now = today ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SyntheticFeed.Central));
        var latest = Stored.Where(r => r.CentralDate.Year >= FirstYear).Max(r => r.CentralDate);
        Through = (latest > now ? now : latest).AddDays(-10);
        Visible = Stored.Where(r => r.CentralDate.Year >= FirstYear && r.CentralDate <= Through).ToList();
        Years = Enumerable.Range(FirstYear, Through.Year - FirstYear + 1).ToArray();
    }

    // ---- predicates ----
    public static bool IsCrime(FeedRecord r) => r.ExpectedIsCrime;

    public static Func<FeedRecord, bool> Group(string key) => r => r.ExpectedGroup == key;

    public static bool IsCar(FeedRecord r) => CarGroups.Contains(r.ExpectedGroup);

    public static Func<FeedRecord, bool> And(Func<FeedRecord, bool> a, Func<FeedRecord, bool>? b) => b is null ? a : r => a(r) && b(r);

    public static Func<FeedRecord, bool> InHood(string name) => r => string.Equals(r.Neighborhood, name, StringComparison.OrdinalIgnoreCase);

    public static Func<FeedRecord, bool> InWard(int ward) => r => r.Ward == ward;

    // ---- aggregates ----
    public bool InSamePeriod(DateOnly d) => d.Month < Through.Month || (d.Month == Through.Month && d.Day <= Through.Day);

    public int Sum(Func<FeedRecord, bool> where, DateOnly from, DateOnly to) =>
        Visible.Where(r => r.CentralDate >= from && r.CentralDate <= to && where(r)).Sum(r => r.Count);

    /// <summary>Offenses from Jan 1 through the through-date's month and day, one number per year.</summary>
    public int[] SamePeriod(Func<FeedRecord, bool> where) =>
        Years.Select(y => Visible.Where(r => r.CentralDate.Year == y && InSamePeriod(r.CentralDate) && where(r)).Sum(r => r.Count)).ToArray();

    /// <summary>Offenses per month, one row per year; months after the data ends are null.</summary>
    public int?[][] Monthly(Func<FeedRecord, bool> where) =>
        Years.Select(y => Enumerable.Range(1, 12).Select(m =>
            y == Through.Year && m > Through.Month
                ? (int?)null
                : Visible.Where(r => r.CentralDate.Year == y && r.CentralDate.Month == m && where(r)).Sum(r => r.Count)).ToArray()).ToArray();

    /// <summary>Per area, the same-period count by year, for rows matching the filter.</summary>
    public Dictionary<string, int[]> ByArea(Func<FeedRecord, bool> where, Func<FeedRecord, string?> area) =>
        Visible.Where(r => InSamePeriod(r.CentralDate) && where(r) && area(r) is not null)
            .GroupBy(r => area(r)!)
            .ToDictionary(g => g.Key, g => Years.Select(y => g.Where(r => r.CentralDate.Year == y).Sum(r => r.Count)).ToArray());

    public static string? HoodOf(FeedRecord r) => r.Neighborhood;

    public static string? WardOf(FeedRecord r) => r.Ward is > 0 ? $"Ward {r.Ward}" : null;

    /// <summary>Offenses in the 365 days ending on the through-date, by weekday (0 = Monday) and hour.</summary>
    public int[][] HourWeekday(Func<FeedRecord, bool> where)
    {
        var grid = Enumerable.Range(0, 7).Select(_ => new int[24]).ToArray();
        foreach (var r in Visible.Where(r => r.CentralDate >= Through.AddDays(-364) && where(r)))
        {
            grid[((int)r.CentralDate.DayOfWeek + 6) % 7][r.CentralHour] += r.Count;
        }

        return grid;
    }

    public static double? PercentChange(int now, int then) => then == 0 ? null : Math.Round((now - then) / (double)then * 100, 1);

    public static double? Rate(int count, int residents, int minimum = 1) =>
        residents >= Math.Max(1, minimum) ? Math.Round(count * 1000.0 / residents, 1) : null;
}
