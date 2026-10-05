using System.Text.Json;
using GlassOnTheStreet.Web.Infrastructure;
using GlassOnTheStreet.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GlassOnTheStreet.Tests.Live;

/// <summary>
/// The city's feed against our database: does every offense MPD published arrive, once, in the right category?
/// Months and categories are compared as the feed itself counts them (rows, and the sum of Crime_Count).
/// </summary>
[Trait("Category", "Live")]
public class LiveSourceTests
{
    private const string Base = "https://services.arcgis.com/afSMGVsC7QlRK1kZ/arcgis/rest/services/Crime_Data/FeatureServer/0/query";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

    private static string Utc(DateOnly centralDay) =>
        TimeZoneInfo.ConvertTimeToUtc(centralDay.ToDateTime(TimeOnly.MinValue), CentralTime.Zone).ToString("yyyy-MM-dd HH:mm:ss");

    private static async Task<List<JsonElement>> StatsAsync(DateOnly from, DateOnly toExclusive, string? groupBy = null)
    {
        var where = $"Occurred_Date >= TIMESTAMP '{Utc(from)}' AND Occurred_Date < TIMESTAMP '{Utc(toExclusive)}'";
        var url = $"{Base}?where={Uri.EscapeDataString(where)}" +
                  "&outStatistics=" + Uri.EscapeDataString("""[{"statisticType":"count","onStatisticField":"OBJECTID","outStatisticFieldName":"n"},{"statisticType":"sum","onStatisticField":"Crime_Count","outStatisticFieldName":"s"}]""") +
                  (groupBy is null ? "" : $"&groupByFieldsForStatistics={groupBy}") + "&f=json";
        var doc = await ArcGisPageFetcher.GetPageAsync(Http, url, NullLogger.Instance, default)
            ?? throw new InvalidOperationException("The city's feed did not answer.");
        return doc.GetProperty("features").EnumerateArray().Select(f => f.GetProperty("attributes").Clone()).ToList();
    }

    [LiveFact(needsSource: true)]
    public async Task EveryMonthMatchesTheFeedInRowsAndOffenses()
    {
        var oracle = LiveWorld.Oracle;
        var stored = LiveWorld.Incidents.Where(r => r.OccurredDate.Year >= 2019)
            .GroupBy(r => (r.OccurredDate.Year, r.OccurredDate.Month)).ToDictionary(g => g.Key, g => (Rows: g.Count(), Offenses: g.Sum(r => (int)r.CrimeCount)));
        var settled = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-60);
        var problems = new List<string>();

        for (var month = new DateOnly(2019, 1, 1); month <= oracle.Through; month = month.AddMonths(1))
        {
            var next = month.AddMonths(1);
            var source = (await StatsAsync(month, next))[0];
            var rows = source.GetProperty("n").GetInt32();
            var offenses = (int)(source.TryGetProperty("s", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0);
            var ours = stored.GetValueOrDefault((month.Year, month.Month));

            // MPD adds records late, so only months well past that window must match exactly. Newer ones may only trail the feed.
            var exact = next <= settled;
            var ok = exact ? ours.Rows == rows && ours.Offenses == offenses : ours.Rows <= rows && ours.Rows >= rows * 0.98;
            if (!ok)
            {
                problems.Add($"{month:yyyy-MM}: feed {rows:N0} rows / {offenses:N0} offenses, database {ours.Rows:N0} rows / {ours.Offenses:N0} offenses ({(exact ? "must match" : "may trail by 2%")})");
            }
        }

        Assert.True(problems.Count == 0, $"{problems.Count} months differ from the city's feed:\n  {string.Join("\n  ", problems)}");
    }

    [LiveFact(needsSource: true)]
    public async Task EveryYearAndOffenseCategoryMatchesTheFeed()
    {
        var settledYear = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-60).Year - 1;
        var problems = new List<string>();

        for (var year = 2019; year <= settledYear; year++)
        {
            var feed = (await StatsAsync(new DateOnly(year, 1, 1), new DateOnly(year + 1, 1, 1), "Offense_Category"))
                .GroupBy(a => (a.GetProperty("Offense_Category").GetString() ?? "").Trim())
                .ToDictionary(g => g.Key, g => (Rows: g.Sum(a => a.GetProperty("n").GetInt32()), Offenses: g.Sum(a => (int)a.GetProperty("s").GetDouble())));
            var ours = LiveWorld.Incidents.Where(r => r.OccurredDate.Year == year)
                .GroupBy(r => r.OffenseCategory ?? "").ToDictionary(g => g.Key, g => (Rows: g.Count(), Offenses: g.Sum(r => (int)r.CrimeCount)));

            foreach (var category in feed.Keys.Union(ours.Keys).Order())
            {
                var f = feed.GetValueOrDefault(category);
                var o = ours.GetValueOrDefault(category);
                if (f != o)
                {
                    problems.Add($"{year} '{category}': feed {f.Rows:N0} rows / {f.Offenses:N0} offenses, database {o.Rows:N0} / {o.Offenses:N0}");
                }
            }
        }

        Assert.True(problems.Count == 0, $"{problems.Count} year/category totals differ from the city's feed:\n  {string.Join("\n  ", problems.Take(40))}");
    }
}
