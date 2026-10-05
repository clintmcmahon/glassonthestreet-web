using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using GlassOnTheStreet.Web.Models;
using GlassOnTheStreet.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GlassOnTheStreet.Tests.DataAccuracy;

/// <summary>
/// The same checks run against the synthetic site (every build) and the real one (the live audit).
/// What a visitor actually sees: the server-rendered HTML, the JSON API and the CSV downloads, read back
/// and compared with the oracle. A number can be right in a service and wrong on the page; this catches that.
/// </summary>
public partial class SiteChecks(HttpClient client, Oracle oracle)
{
    private Oracle O => oracle;

    private async Task<IDocument> PageAsync(string url)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.IsSuccessStatusCode, $"{url} returned {(int)response.StatusCode}");
        var html = await response.Content.ReadAsStringAsync();
        return await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html));
    }

    private async Task<JsonElement> JsonAsync(string url)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.IsSuccessStatusCode, $"{url} returned {(int)response.StatusCode}");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private static int N(string? text) => int.Parse(NonDigits().Replace(text ?? "", ""), CultureInfo.InvariantCulture);

    [GeneratedRegex(@"[^\d]")]
    private static partial Regex NonDigits();

    private static string Text(IElement? e) => Regex.Replace(e?.TextContent ?? "", @"\s+", " ").Trim();

    private static string Metric(IDocument doc, string key) =>
        Text(doc.QuerySelector($"[data-metric='{key}']") ?? throw new Xunit.Sdk.XunitException($"No element with data-metric '{key}' on the page."));

    private static string Pct(int now, int then) =>
        then == 0 ? "n/a" : $"{(now >= then ? "+" : "-")}{Math.Abs(Math.Round((now - then) / (double)then * 100)).ToString("0", CultureInfo.InvariantCulture)}%";

    private string Day(DateOnly d) => d.ToString("MMM d", CultureInfo.InvariantCulture);

    private int CarSamePeriod(int yearIndex) => O.SamePeriod(Oracle.IsCar)[yearIndex];

    private static int[] Ints(JsonElement e) => e.EnumerateArray().Select(x => x.GetInt32()).ToArray();

    private async Task<List<string[]>> CsvAsync(string url)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.IsSuccessStatusCode, $"{url} returned {(int)response.StatusCode}");
        return (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(',')).ToList();
    }

    private IEnumerable<string> CrimeGroupKeys() => O.Visible.Where(r => r.ExpectedIsCrime).Select(r => r.ExpectedGroup).Distinct();

    public async Task Home_CarThirtyDayFiguresMatchTheOracle()
    {
        var doc = await PageAsync("/");
        var now = O.Sum(Oracle.IsCar, O.Through.AddDays(-29), O.Through);
        var before = O.Sum(Oracle.IsCar, O.Through.AddDays(-59), O.Through.AddDays(-30));
        var change = Oracle.PercentChange(now, before);

        Assert.Equal(now, N(Metric(doc, "car-30d")));
        var shown = Metric(doc, "car-30d-change");
        Assert.Equal(change is null ? "—" : $"{(change > 0 ? "+" : "")}{change}%", shown);
    }

    public async Task Home_YearToDateFiguresMatchTheOracleForAllCrimesAndForCarCrime()
    {
        var doc = await PageAsync("/");
        var all = O.SamePeriod(Oracle.IsCrime);
        var car = O.SamePeriod(Oracle.IsCar);

        Assert.Equal(all[^1], N(Metric(doc, "all-ytd")));
        Assert.Equal(Pct(all[^1], all[^2]), Metric(doc, "all-ytd-vs-prior"));
        Assert.Equal(Pct(all[^1], all[0]), Metric(doc, "all-ytd-vs-base"));
        Assert.Equal(car[^1], N(Metric(doc, "car-ytd")));
        Assert.Equal(Pct(car[^1], car[^2]), Metric(doc, "car-ytd-vs-prior"));
        Assert.Equal(Pct(car[^1], car[0]), Metric(doc, "car-ytd-vs-base"));
    }

    public async Task Home_CarCrimeIsASubsetOfAllCrimeOnThePage()
    {
        var doc = await PageAsync("/");

        Assert.True(N(Metric(doc, "car-ytd")) < N(Metric(doc, "all-ytd")));
        // The 30-day car count can never exceed a full year-to-date car count.
        Assert.True(N(Metric(doc, "car-30d")) <= N(Metric(doc, "car-ytd")) + N(Metric(doc, "car-ytd")));
    }

    public async Task Home_AllCrimeTypeListShowsTheEightLargestGroupsWithTheirRealCounts()
    {
        var doc = await PageAsync("/");
        var shown = doc.QuerySelectorAll("[data-metric^='all-group:']")
            .Select(e => (Key: e.GetAttribute("data-metric")![10..], Count: N(Text(e.QuerySelector(".breakdown-count")))))
            .ToList();
        var expected = CrimeGroupKeys()
            .Select(k => (Key: k, Count: O.SamePeriod(Oracle.Group(k))[^1]))
            .OrderByDescending(x => x.Count).Take(8).ToList();

        Assert.Equal(8, shown.Count);
        Assert.Equal(expected.Select(x => x.Count), shown.Select(x => x.Count));
        Assert.All(shown, s => Assert.Equal(O.SamePeriod(Oracle.Group(s.Key))[^1], s.Count));
        Assert.DoesNotContain(shown, s => !O.Visible.First(r => r.ExpectedGroup == s.Key).ExpectedIsCrime);
    }

    public async Task Home_MonthlyBarsMatchTheOracle()
    {
        var doc = await PageAsync("/");
        var lastMonth = O.Through.AddDays(1).Day == 1 ? new DateOnly(O.Through.Year, O.Through.Month, 1) : new DateOnly(O.Through.Year, O.Through.Month, 1).AddMonths(-1);
        var bars = doc.QuerySelectorAll("[data-metric^='car-month:']").ToList();

        Assert.Equal(6, bars.Count);
        for (var i = 0; i < 6; i++)
        {
            var start = lastMonth.AddMonths(i - 5);
            Assert.Equal(start.ToString("MMM yyyy"), bars[i].GetAttribute("data-metric")![10..]);
            Assert.Equal(O.Sum(Oracle.IsCar, start, start.AddMonths(1).AddDays(-1)), N(Text(bars[i].QuerySelector(".trend-bar-count"))));
        }
    }

    public async Task Home_TopFiveNeighborhoodsAndWardsMatchTheOracle()
    {
        var doc = await PageAsync("/");
        var lists = doc.QuerySelectorAll("ol.home-areas").ToList();
        Assert.Equal(2, lists.Count);

        List<(string Name, int Count)> Shown(IElement list) => list.QuerySelectorAll("li")
            .Select(li => (Text(li.QuerySelector("a")), N(Text(li.QuerySelector("strong"))))).ToList();

        List<(string, int)> Expected(Func<FeedRecord, string?> area) => O.ByArea(Oracle.IsCar, area)
            .OrderByDescending(kv => kv.Value[^1]).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(5).Select(kv => (kv.Key, kv.Value[^1])).ToList();

        Assert.Equal(Expected(Oracle.HoodOf), Shown(lists[0]));
        Assert.Equal(Expected(Oracle.WardOf), Shown(lists[1]));
    }

    public async Task Home_DatesOnThePageAreTheThroughDateAndNeverTheHeldBackDays()
    {
        var doc = await PageAsync("/");
        var text = Text(doc.Body);
        var through = Day(O.Through);

        Assert.Contains($"Jan 1 to {through}", text);
        Assert.Contains($"MPD data through {through}", text);
        // No date inside the 10-day holdback appears as a data date.
        foreach (var held in Enumerable.Range(1, 10).Select(d => O.Through.AddDays(d)))
        {
            Assert.DoesNotContain($"to {Day(held)}", text);
            Assert.DoesNotContain($"through {Day(held)}", text);
        }
    }

    public async Task Home_EveryNumberSaysWhetherItIsMpdDataOrResidentReportsAndWhichCrimes()
    {
        var doc = await PageAsync("/");

        foreach (var tile in doc.QuerySelectorAll(".stat").Where(s => s.QuerySelector("[data-metric]") is not null))
        {
            var key = tile.QuerySelector("[data-metric]")!.GetAttribute("data-metric");
            var label = Text(tile.QuerySelector(".stat-label"));
            if (key == "never-told-police")
            {
                Assert.Contains("resident", label);
                continue;
            }

            Assert.True(label.Contains("MPD") || label.Contains("recorded by MPD"), $"{key}: '{label}' does not name its source.");
            Assert.True(label.Contains("car-related") || label.Contains("all offenses") || label.Contains("all types"), $"{key}: '{label}' does not say which crimes.");
        }

        foreach (var heading in doc.QuerySelectorAll("h2.graph-heading, h3.graph-heading").Where(h => !Text(h).StartsWith("Explore")))
        {
            Assert.True(Text(heading).Contains("MPD data") || Text(heading).Contains("MPD"), $"Heading '{Text(heading)}' does not name its source.");
        }
    }

    public async Task CrimePage_GroupTableAndYearTableMatchTheOracle()
    {
        var doc = await PageAsync("/crime");
        var rows = doc.QuerySelectorAll("table.groups-table tbody tr:not(.near-sep)").ToList();
        var seen = 0;

        foreach (var row in rows)
        {
            var cells = row.QuerySelectorAll("td").ToList();
            var group = CrimeGroups.All.First(g => g.Label == Text(cells[0]));
            var counts = O.SamePeriod(Oracle.Group(group.Key));
            Assert.Equal(counts[^1], N(Text(cells[1])));
            Assert.Equal(counts[^2], N(Text(cells[2])));
            seen++;
        }

        Assert.Equal(CrimeGroups.All.Count, seen);

        var period = doc.QuerySelectorAll("#chart-period table tbody tr").Select(r => r.QuerySelectorAll("td").ToList()).ToList();
        Assert.Equal(O.Years.Length, period.Count);
        Assert.Equal(O.SamePeriod(Oracle.IsCrime), period.Select(c => N(Text(c[1]))));
        Assert.Equal(O.Years, period.Select(c => N(Text(c[0]))));
    }

    public async Task CrimePage_MonthlyTableAndAreaTableMatchTheOracle()
    {
        var doc = await PageAsync("/crime");
        var monthly = O.Monthly(Oracle.IsCrime);
        var table = doc.QuerySelectorAll("table").First(t => Text(t.QuerySelector("th")) == "Month");
        var rows = table.QuerySelectorAll("tbody tr").ToList();

        Assert.Equal(12, rows.Count);
        for (var m = 0; m < 12; m++)
        {
            var cells = rows[m].QuerySelectorAll("td").Skip(1).Select(Text).ToList();
            for (var y = 0; y < O.Years.Length; y++)
            {
                Assert.Equal(monthly[y][m] is { } v ? v.ToString("N0", CultureInfo.InvariantCulture) : "", cells[y]);
            }
        }

        var areas = doc.QuerySelectorAll("table").First(t => Text(t.QuerySelector("th")) == "Area").QuerySelectorAll("tbody tr").ToList();
        var expected = O.ByArea(Oracle.IsCrime, Oracle.HoodOf).OrderByDescending(kv => kv.Value[^1]).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Take(10).ToList();
        Assert.Equal(expected.Select(e => e.Key), areas.Take(expected.Count).Select(r => Text(r.QuerySelector("a"))));
        Assert.Equal(expected.Select(e => e.Value[^1]), areas.Take(expected.Count).Select(r => N(Text(r.QuerySelectorAll("td")[^2]))));
    }

    public async Task TrendsPage_CarTablesMatchTheOracle()
    {
        var doc = await PageAsync("/trends");
        var period = doc.QuerySelectorAll("#chart-period table tbody tr").Select(r => r.QuerySelectorAll("td").ToList()).ToList();
        var monthly = O.Monthly(Oracle.IsCar);

        Assert.Equal(O.SamePeriod(Oracle.IsCar), period.Select(c => N(Text(c[1]))));
        var table = doc.QuerySelectorAll("table").First(t => Text(t.QuerySelector("th")) == "Month");
        var rows = table.QuerySelectorAll("tbody tr").ToList();
        for (var m = 0; m < 12; m++)
        {
            var cells = rows[m].QuerySelectorAll("td").Skip(1).Select(Text).ToList();
            for (var y = 0; y < O.Years.Length; y++)
            {
                Assert.Equal(monthly[y][m] is { } v ? v.ToString("N0", CultureInfo.InvariantCulture) : "", cells[y]);
            }
        }
    }

    public async Task AreaPage_TablesMatchTheOracle(string path, string? hood, int? ward)
    {
        var doc = await PageAsync("/" + path);
        var scope = Oracle.And(r => true, hood is null ? (r => r.Ward == ward) : Oracle.InHood(hood));

        var groupRows = doc.QuerySelectorAll("table.groups-table tbody tr:not(.near-sep)").ToList();
        Assert.NotEmpty(groupRows);
        foreach (var row in groupRows)
        {
            var cells = row.QuerySelectorAll("td").ToList();
            var group = CrimeGroups.All.First(g => g.Label == Text(cells[0]));
            var counts = O.SamePeriod(Oracle.And(Oracle.Group(group.Key), scope));
            Assert.Equal(counts[^1], N(Text(cells[1])));
            Assert.Equal(counts[^2], N(Text(cells[2])));
        }

        // The car-only "by year" table.
        var period = doc.QuerySelectorAll("table.area-table").First(t => Text(t.QuerySelector("th")) == "Year").QuerySelectorAll("tbody tr").ToList();
        Assert.Equal(O.SamePeriod(Oracle.And(Oracle.IsCar, scope)), period.Select(r => N(Text(r.QuerySelectorAll("td")[1]))));

        // Category table: first-year, prior-year and current counts for each of the four car groups.
        foreach (var row in doc.QuerySelectorAll("table.area-table").First(t => Text(t.QuerySelector("th")) == "Category").QuerySelectorAll("tbody tr"))
        {
            var cells = row.QuerySelectorAll("td").ToList();
            var key = Text(cells[0]) switch
            {
                "Theft from motor vehicle" => "theft-from-vehicle",
                "Motor vehicle theft" => "vehicle-theft",
                "Vehicle parts theft" => "parts-theft",
                "Property damage / vandalism" => "vandalism",
                var other => throw new Xunit.Sdk.XunitException($"Unexpected category '{other}'")
            };
            var counts = O.SamePeriod(Oracle.And(Oracle.Group(key), scope));
            Assert.Equal(counts[0], N(Text(cells[1])));
            Assert.Equal(counts[^2], N(Text(cells[2])));
            Assert.Equal(counts[^1], N(Text(cells[3])));
        }
    }

    public async Task CrimeApi_MatchesTheOracle(string query, string? group, string? hood, int? ward)
    {
        var json = await JsonAsync("/api/crime" + query);
        Func<FeedRecord, bool> pred = group is null ? Oracle.IsCrime : Oracle.Group(group);
        var scope = Oracle.And(pred, r => (hood is null || Oracle.InHood(hood)(r)) && (ward is null || r.Ward == ward));

        Assert.Equal(O.Years, Ints(json.GetProperty("years")));
        Assert.Equal(O.Through.ToString("yyyy-MM-dd"), json.GetProperty("through").GetString());
        Assert.Equal(O.SamePeriod(scope), Ints(json.GetProperty("samePeriod")));

        var monthly = O.Monthly(scope);
        var apiMonthly = json.GetProperty("monthly").EnumerateArray().Select(y => y.EnumerateArray().Select(m => m.ValueKind == JsonValueKind.Null ? (int?)null : m.GetInt32()).ToArray()).ToArray();
        Assert.Equal(monthly, apiMonthly);
        Assert.Equal(O.HourWeekday(scope).Sum(d => d.Sum()), json.GetProperty("hourWeekdayTotal").GetInt32());
    }

    public async Task TrendsApi_MatchesTheOracle(string query, string? group, string? hood, int? ward)
    {
        var json = await JsonAsync("/api/trends" + query);
        Func<FeedRecord, bool> pred = group is null ? Oracle.IsCar : Oracle.Group(group);
        var scope = Oracle.And(pred, r => (hood is null || Oracle.InHood(hood)(r)) && (ward is null || r.Ward == ward));

        Assert.Equal(O.SamePeriod(scope), Ints(json.GetProperty("samePeriod")));
    }

    public async Task MonthlyCsv_MatchesTheOracleRowForRow()
    {
        var rows = (await CsvAsync("/data/crime-monthly.csv")).Skip(1).ToList();
        var expected = O.Visible.GroupBy(r => (r.CentralDate.Year, r.CentralDate.Month, r.ExpectedGroup)).ToDictionary(g => g.Key, g => g.Sum(r => r.Count));

        Assert.Equal(expected.Count, rows.Count);
        foreach (var row in rows)
        {
            var key = (int.Parse(row[0]), int.Parse(row[1]), row[2]);
            Assert.Equal(expected[key], int.Parse(row[5]));
            Assert.Equal(O.Visible.First(r => r.ExpectedGroup == row[2]).ExpectedIsCrime ? "true" : "false", row[4]);
        }

        Assert.Equal(O.Visible.Sum(r => r.Count), rows.Sum(r => int.Parse(r[5])));
    }

    public async Task AreaCsvs_MatchTheOracle()
    {
        var hoods = (await CsvAsync("/data/crime-by-neighborhood.csv")).Skip(1).ToList();
        var expectedHoods = O.Visible.Where(r => r.Neighborhood is not null).GroupBy(r => (r.Neighborhood!, r.CentralDate.Year, r.ExpectedGroup)).ToDictionary(g => g.Key, g => g.Sum(r => r.Count));
        Assert.Equal(expectedHoods.Count, hoods.Count);
        Assert.All(hoods, row => Assert.Equal(expectedHoods[(row[0], int.Parse(row[1]), row[2])], int.Parse(row[5])));

        var wards = (await CsvAsync("/data/crime-by-ward.csv")).Skip(1).ToList();
        var expectedWards = O.Visible.Where(r => r.Ward is > 0).GroupBy(r => (r.Ward!.Value, r.CentralDate.Year, r.ExpectedGroup)).ToDictionary(g => g.Key, g => g.Sum(r => r.Count));
        Assert.Equal(expectedWards.Count, wards.Count);
        Assert.All(wards, row => Assert.Equal(expectedWards[(int.Parse(row[0]), int.Parse(row[1]), row[2])], int.Parse(row[5])));
    }

    public async Task MonthlyReportPage_QuotesTheOraclesTotalsForItsMonth()
    {
        var month = new DateOnly(O.Through.Year, O.Through.Month, 1).AddMonths(-1);
        var doc = await PageAsync($"/monthly/{month.Year}-{month.Month:00}");
        var text = Text(doc.Body);
        var total = O.Sum(Oracle.IsCrime, month, month.AddMonths(1).AddDays(-1));

        Assert.Contains(total.ToString("N0", CultureInfo.InvariantCulture), text);
    }

    public async Task MapApi_PinsPlusUnlocatedEqualTheSummaryAndTheOracle(string query, string group, int days)
    {
        var blocks = await JsonAsync("/api/map/blocks" + query);
        var summary = await JsonAsync("/api/map/summary" + query);
        var pred = group switch { "" => (Func<FeedRecord, bool>)Oracle.IsCrime, "car" => Oracle.IsCar, _ => Oracle.Group(group) };
        var from = O.Through.AddDays(-(days - 1));
        var inRange = O.Visible.Where(r => r.CentralDate >= from && r.CentralDate <= O.Through && pred(r)).ToList();
        var all = inRange.Sum(r => r.Count);
        var located = inRange.Where(r => r.Located).Sum(r => r.Count);
        var meta = blocks.GetProperty("meta");

        Assert.Equal(all, summary.GetProperty("total").GetInt32());
        Assert.Equal(located, meta.GetProperty("total").GetInt32());
        Assert.Equal(all - located, meta.GetProperty("unlocated").GetInt32());
        Assert.Equal(all, meta.GetProperty("total").GetInt32() + meta.GetProperty("unlocated").GetInt32());
        Assert.Equal(located, blocks.GetProperty("features").EnumerateArray().Sum(f => f.GetProperty("properties").GetProperty("n").GetInt32()));
        Assert.Equal(O.Through.ToString("yyyy-MM-dd"), meta.GetProperty("to").GetString());

        // Every pin sits in Minneapolis. A pin at 0,0 would be a record MPD could not place, drawn anyway.
        foreach (var feature in blocks.GetProperty("features").EnumerateArray())
        {
            var c = feature.GetProperty("geometry").GetProperty("coordinates");
            Assert.InRange(c[1].GetDouble(), 44.85, 45.07);
            Assert.InRange(c[0].GetDouble(), -93.40, -93.15);
        }
    }
}
