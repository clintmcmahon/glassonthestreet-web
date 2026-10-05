using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GlassOnTheStreet.Tests.DataAccuracy;

/// <summary>
/// One row of MPD's feed, built by the test with the answer attached. <see cref="ExpectedGroup"/> and
/// <see cref="ExpectedIsCrime"/> are chosen when the row is generated, from the offense text, and never
/// derived by the code under test: they are the ground truth the pipeline is checked against.
/// </summary>
public sealed record FeedRecord(
    string Case, string Address, long OccurredMs, string Offense, string Category, string Type, string NibrsCode,
    string CrimeAgainst, int Count, string? Neighborhood, int? Ward, int? Precinct, double? X, double? Y,
    string ExpectedGroup, bool ExpectedIsCrime, DateOnly CentralDate, int CentralHour)
{
    public string Key => $"{Case}|{NibrsCode}|{Offense}";

    /// <summary>True when MPD gave the record a real location (the feed sends 0,0 for the rest).</summary>
    public bool Located => X is { } x && Y is { } y && !(x == 0 && y == 0);
}

public sealed record Block(string Neighborhood, int Ward, string Address, double Lat, double Lng);

/// <summary>A reproducible stand-in for the city's Crime_Data feature service, with edge cases on purpose.</summary>
public static class SyntheticFeed
{
    public static readonly TimeZoneInfo Central = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    /// <summary>Last record in the fake feed. The site holds back the newest 10 days, so these rows must not be counted.</summary>
    public static readonly DateOnly Latest = new(2026, 9, 30);

    /// <summary>(group, category, offense, type, NIBRS code, crime against, weight)</summary>
    public static readonly (string Group, string Category, string Offense, string Type, string Nibrs, string Against, int Weight)[] Templates =
    [
        ("homicide", "Homicide Offenses", "Homicide Offense", "Crime Offenses (NIBRS)", "09A", "Person", 1),
        ("agg-assault", "Assault Offenses", "Aggravated Assault", "Crime Offenses (NIBRS)", "13A", "Person", 8),
        ("simple-assault", "Assault Offenses", "Simple Assault", "Crime Offenses (NIBRS)", "13B", "Person", 12),
        ("intimidation", "Assault Offenses", "Intimidation", "Crime Offenses (NIBRS)", "13C", "Person", 6),
        ("robbery", "Robbery", "Robbery", "Crime Offenses (NIBRS)", "120", "Person", 4),
        ("sex-offenses", "Sex Offenses", "Rape", "Crime Offenses (NIBRS)", "11A", "Person", 2),
        ("burglary", "Burglary/Breaking & Entering", "Burglary/Breaking & Entering", "Crime Offenses (NIBRS)", "220", "Property", 6),
        ("theft-from-vehicle", "Larceny/Theft Offenses", "Theft From Motor Vehicle", "Crime Offenses (NIBRS)", "23F", "Property", 12),
        ("vehicle-theft", "Motor Vehicle Theft", "Motor Vehicle Theft", "Crime Offenses (NIBRS)", "240", "Property", 10),
        ("parts-theft", "Larceny/Theft Offenses", "Theft of Motor Vehicle Parts or Accessories", "Crime Offenses (NIBRS)", "23G", "Property", 5),
        ("shoplifting", "Larceny/Theft Offenses", "Shoplifting", "Crime Offenses (NIBRS)", "23C", "Property", 6),
        ("other-larceny", "Larceny/Theft Offenses", "All Other Larceny", "Crime Offenses (NIBRS)", "23H", "Property", 12),
        ("vandalism", "Destruction/Damage/Vandalism of Property", "Destruction/Damage/Vandalism of Property", "Crime Offenses (NIBRS)", "290", "Property", 14),
        ("arson", "Arson", "Arson", "Crime Offenses (NIBRS)", "200", "Property", 1),
        ("fraud", "Fraud Offenses", "Credit Card/Automated Teller Machine Fraud", "Crime Offenses (NIBRS)", "26B", "Property", 4),
        ("drugs", "Drug/Narcotic Offenses", "Drug/Narcotic Violations", "Crime Offenses (NIBRS)", "35A", "Society", 4),
        ("weapons", "Weapon Law Violations", "Weapon Law Violations", "Crime Offenses (NIBRS)", "520", "Society", 3),
        ("other", "Kidnapping/Abduction", "Kidnapping/Abduction", "Crime Offenses (NIBRS)", "100", "Person", 2),
        ("shots-fired", "Shots Fired Calls", "ShotSpotter Activation (P)", "Shots Fired Calls", "", "Non NIBRS Data", 5),
        ("gunshot-victims", "Gunshot Wound Victims", "Gunshot Wound Victims", "Gunshot Wound Victims", "", "Non NIBRS Data", 2),
        ("domestic-agg-assault", "Subset of NIBRS Assault Offenses", "Domestic Aggravated Assault - Subset of Assault", "Additional Crime Metrics", "13A", "Non NIBRS Data", 2),
        ("carjacking", "Subset of NIBRS Robbery", "Carjacking - Subset of Robbery", "Additional Crime Metrics", "120", "Non NIBRS Data", 1),
    ];

    /// <summary>Rows MPD publishes that are not crimes, or that repeat a crime counted under its parent.</summary>
    public static readonly HashSet<string> NonCrimeGroups = ["shots-fired", "gunshot-victims", "domestic-agg-assault", "carjacking"];

    /// <summary>Real neighborhood names, so the population data (and therefore rates) apply; wards are arbitrary but fixed.</summary>
    public static readonly Block[] Blocks =
    [
        new("Whittier", 10, "0026XX NICOLLET AVE", 44.9552, -93.2776),
        new("Whittier", 10, "0030XX 1ST AVE S", 44.9490, -93.2744),
        new("Longfellow", 12, "0048XX 13TH AVE S", 44.9122, -93.2431),
        new("Longfellow", 12, "0035XX 34TH AVE S", 44.9380, -93.2290),
        new("Linden Hills", 13, "0030XX XERXES AVE S", 44.9305, -93.3217),
        new("Phillips West", 6, "0012XX 24TH ST E", 44.9524, -93.2610),
        new("Phillips West", 6, "0015XX PORTLAND AVE", 44.9618, -93.2630),
        new("Bottineau", 3, "0021XX CALIFORNIA ST NE", 45.0057, -93.2645),
        new("Downtown West", 7, "0004XX HENNEPIN AVE", 44.9803, -93.2745),
        new("Marcy Holmes", 3, "0008XX 5TH ST SE", 44.9871, -93.2420),
    ];

    public static List<FeedRecord> Generate(int count = 14_000, int seed = 20260930)
    {
        var rng = new Random(seed);
        var weighted = Templates.SelectMany(t => Enumerable.Repeat(t, t.Weight)).ToArray();
        var records = new List<FeedRecord>(count + 200);
        var first = new DateOnly(2018, 6, 1);
        var span = Latest.DayNumber - first.DayNumber + 1;

        FeedRecord Make(string caseNumber, DateOnly date, int hour, int minute, int templateIndex, int? blockIndex, int offenseCount)
        {
            var t = weighted[templateIndex];
            var local = date.ToDateTime(new TimeOnly(hour, minute));
            var utc = TimeZoneInfo.ConvertTimeToUtc(local, Central);
            var ms = new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds();
            Block? b = blockIndex is { } i ? Blocks[i] : null;
            var (x, y) = b is null ? ((double?)null, (double?)null) : Mercator(b.Lat, b.Lng);
            return new FeedRecord(
                caseNumber, b?.Address ?? "", ms, t.Offense, t.Category, t.Type, t.Nibrs, t.Against, offenseCount,
                b?.Neighborhood, b?.Ward, b is null ? null : b.Ward % 5 + 1, x, y, t.Group, !NonCrimeGroups.Contains(t.Group),
                date, hour);
        }

        for (var n = 0; n < count; n++)
        {
            var date = first.AddDays(rng.Next(span));
            var hour = rng.Next(24);
            var offenseCount = rng.NextDouble() switch { < 0.86 => 1, < 0.96 => 2, _ => 3 };
            // About 3% have no location or neighborhood, as in the real feed.
            int? block = rng.NextDouble() < 0.03 ? null : rng.Next(Blocks.Length);
            records.Add(Make($"{date.Year % 100:00}-{n:000000}", date, hour, rng.Next(60), rng.Next(weighted.Length), block, offenseCount));
        }

        // Edge cases the pipeline must get right.
        var edgeTheft = Array.FindIndex(weighted, t => t.Group == "theft-from-vehicle");
        // 11:30 pm and 12:30 am Central on a New Year: the stored date is the Central one, not the UTC one.
        records.Add(Make("20-EDGE01", new DateOnly(2020, 12, 31), 23, 30, edgeTheft, 0, 1));
        records.Add(Make("20-EDGE02", new DateOnly(2021, 1, 1), 0, 30, edgeTheft, 0, 1));
        // Daylight saving transitions.
        records.Add(Make("21-EDGE03", new DateOnly(2021, 3, 14), 3, 15, edgeTheft, 1, 1));
        records.Add(Make("21-EDGE04", new DateOnly(2021, 11, 7), 0, 45, edgeTheft, 1, 1));
        // The first and last second of the data's window.
        records.Add(Make("19-EDGE05", new DateOnly(2019, 1, 1), 0, 0, edgeTheft, 2, 1));
        records.Add(Make("26-EDGE06", Latest, 23, 59, edgeTheft, 2, 1));
        // A case with two different offenses is two rows, both kept.
        var multi = records[^1] with { Case = "26-MULTI1", Offense = "Theft of Motor Vehicle Parts or Accessories", NibrsCode = "23G", ExpectedGroup = "parts-theft", Category = "Larceny/Theft Offenses" };
        records.Add(multi);
        records.Add(multi with { Offense = "Destruction/Damage/Vandalism of Property", NibrsCode = "290", Category = "Destruction/Damage/Vandalism of Property", ExpectedGroup = "vandalism" });

        // The feed lists some offenses twice under one case (two weapons offenses, two ShotSpotter activations).
        // The city's own totals count each row, so each is a separate offense here too.
        records.AddRange(records.Where((_, i) => i % 97 == 0).ToList());
        return records;
    }

    /// <summary>The same offense moved to another Central date and hour, with its timestamp recomputed.</summary>
    public static FeedRecord OnDate(FeedRecord r, DateOnly date, int hour = 12)
    {
        var utc = TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(new TimeOnly(hour, 0)), Central);
        return r with { OccurredMs = new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds(), CentralDate = date, CentralHour = hour };
    }

    private static (double X, double Y) Mercator(double lat, double lng)
    {
        const double r = 20037508.34;
        var x = lng * r / 180.0;
        var y = Math.Log(Math.Tan((90 + lat) * Math.PI / 360.0)) / (Math.PI / 180.0) * r / 180.0;
        return (x, y);
    }

    /// <summary>Serves the records the way the ArcGIS query endpoint does: a where clause on Occurred_Date, paging by resultOffset.</summary>
    public sealed class FeedHandler(IReadOnlyList<FeedRecord> records) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        /// <summary>Optional: fail the request at this page offset (HTTP 504) to test partial-import behavior.</summary>
        public Func<int, bool>? FailAtOffset { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            var window = Regex.Matches(query, @"TIMESTAMP '([^']+)'");
            var from = DateTime.Parse(window[0].Groups[1].Value);
            var to = DateTime.Parse(window[1].Groups[1].Value);

            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            // The feed's own statistics: how many rows and what Crime_Count adds up to, in a window.
            if (query.Contains("outStatistics"))
            {
                var inWindow = records.Where(r => epoch.AddMilliseconds(r.OccurredMs) >= from && epoch.AddMilliseconds(r.OccurredMs) < to).ToList();
                var groupBy = Regex.Match(query, @"groupByFieldsForStatistics=([^&]+)");
                var fields = groupBy.Success ? groupBy.Groups[1].Value.Split(',') : [];

                // The city's service pads some text with a trailing space; the importer trims, so the fake does the same padding.
                object? Value(FeedRecord r, string f) => f switch
                {
                    "Offense_Category" => r.Category + " ",
                    "Offense" => r.Offense,
                    "Neighborhood" => r.Neighborhood,
                    "Ward" => r.Ward,
                    _ => throw new NotSupportedException(f)
                };

                var rows = inWindow.GroupBy(r => string.Join("\u0001", fields.Select(f => Value(r, f))))
                    .Select(g =>
                    {
                        var row = new Dictionary<string, object?>();
                        foreach (var f in fields)
                        {
                            row[f] = Value(g.First(), f);
                        }

                        row["n"] = g.Count();
                        row["s"] = (double)g.Sum(r => r.Count);
                        return row;
                    }).ToList();
                if (fields.Length == 0 && rows.Count == 0)
                {
                    rows.Add(new Dictionary<string, object?> { ["n"] = 0, ["s"] = null });
                }

                var stats = JsonSerializer.Serialize(new { features = rows.Select(a => new { attributes = a }) });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stats, Encoding.UTF8, "application/json") });
            }

            var offset = int.Parse(Regex.Match(query, @"resultOffset=(\d+)").Groups[1].Value);
            var size = int.Parse(Regex.Match(query, @"resultRecordCount=(\d+)").Groups[1].Value);

            if (FailAtOffset?.Invoke(offset) == true)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.GatewayTimeout));
            }


            var page = records
                .Select((r, i) => (r, i))
                .Where(x => epoch.AddMilliseconds(x.r.OccurredMs) >= from && epoch.AddMilliseconds(x.r.OccurredMs) < to)
                .OrderBy(x => x.r.OccurredMs).ThenBy(x => x.i)
                .Skip(offset).Take(size)
                .Select(x => Attributes(x.r))
                .ToList();

            var json = JsonSerializer.Serialize(new { features = page.Select(a => new { attributes = a }) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }

        private static Dictionary<string, object?> Attributes(FeedRecord r) => new()
        {
            ["Case_Number"] = r.Case,
            // MPD sends "No Address" and 0,0 for a record it could not place.
            ["Address"] = r.Located ? r.Address : "No Address",
            ["Occurred_Date"] = r.OccurredMs,
            ["Offense"] = r.Offense,
            ["Offense_Category"] = r.Category,
            ["Type"] = r.Type,
            ["Neighborhood"] = r.Neighborhood,
            ["Ward"] = r.Ward,
            ["Precinct"] = r.Precinct,
            ["wgsXAnon"] = r.Located ? r.X : 0,
            ["wgsYAnon"] = r.Located ? r.Y : 0,
            ["NIBRS_Code"] = r.NibrsCode,
            ["NIBRS_Crime_Against"] = r.CrimeAgainst,
            ["Crime_Count"] = r.Count
        };
    }
}
