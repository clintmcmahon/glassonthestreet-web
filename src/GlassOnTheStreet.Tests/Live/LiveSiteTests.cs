using System.Text.Json;
using GlassOnTheStreet.Tests.DataAccuracy;
using GlassOnTheStreet.Web.Models;
using GlassOnTheStreet.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace GlassOnTheStreet.Tests.Live;

/// <summary>
/// A running site (GOTS_AUDIT_SITE) against numbers worked out from the database it reads. Run them right after
/// the site's data has settled: the site caches its dataset for 30 minutes, so a fresh import can differ briefly.
/// </summary>
[Trait("Category", "Live")]
public class LiveSiteTests : IDisposable
{
    private readonly HttpClient client = LiveWorld.SiteUrl is null ? new HttpClient() : LiveWorld.NewSiteClient();

    private SiteChecks Checks => new(client, LiveWorld.Oracle);

    public void Dispose() => client.Dispose();

    private static IEnumerable<string> TopHoods(int n) => LiveWorld.Oracle.ByArea(Oracle.IsCrime, Oracle.HoodOf).OrderByDescending(kv => kv.Value[^1]).Take(n).Select(kv => kv.Key);

    [LiveFact(needsSite: true)] public Task Home_CarThirtyDay() => Checks.Home_CarThirtyDayFiguresMatchTheOracle();

    [LiveFact(needsSite: true)] public Task Home_YearToDate() => Checks.Home_YearToDateFiguresMatchTheOracleForAllCrimesAndForCarCrime();

    [LiveFact(needsSite: true)] public Task Home_CarIsSubset() => Checks.Home_CarCrimeIsASubsetOfAllCrimeOnThePage();

    [LiveFact(needsSite: true)] public Task Home_AllCrimeTypes() => Checks.Home_AllCrimeTypeListShowsTheEightLargestGroupsWithTheirRealCounts();

    [LiveFact(needsSite: true)] public Task Home_MonthlyBars() => Checks.Home_MonthlyBarsMatchTheOracle();

    [LiveFact(needsSite: true)] public Task Home_TopAreas() => Checks.Home_TopFiveNeighborhoodsAndWardsMatchTheOracle();

    [LiveFact(needsSite: true)] public Task Home_Dates() => Checks.Home_DatesOnThePageAreTheThroughDateAndNeverTheHeldBackDays();

    [LiveFact(needsSite: true)] public Task Home_Labels() => Checks.Home_EveryNumberSaysWhetherItIsMpdDataOrResidentReportsAndWhichCrimes();

    [LiveFact(needsSite: true)] public Task CrimePage_Groups() => Checks.CrimePage_GroupTableAndYearTableMatchTheOracle();

    [LiveFact(needsSite: true)] public Task CrimePage_MonthlyAndAreas() => Checks.CrimePage_MonthlyTableAndAreaTableMatchTheOracle();

    [LiveFact(needsSite: true)] public Task TrendsPage_Tables() => Checks.TrendsPage_CarTablesMatchTheOracle();

    [LiveFact(needsSite: true)] public Task MonthlyCsv() => Checks.MonthlyCsv_MatchesTheOracleRowForRow();

    [LiveFact(needsSite: true)] public Task AreaCsvs() => Checks.AreaCsvs_MatchTheOracle();

    [LiveFact(needsSite: true)] public Task MonthlyReport() => Checks.MonthlyReportPage_QuotesTheOraclesTotalsForItsMonth();

    [LiveFact(needsSite: true)]
    public async Task AreaPages_ForTheBusiestNeighborhoodsAndWards()
    {
        foreach (var hood in TopHoods(3))
        {
            await Checks.AreaPage_TablesMatchTheOracle($"neighborhoods/{AreaSlug.For(hood)}", hood, null);
        }

        foreach (var ward in LiveWorld.Oracle.ByArea(Oracle.IsCrime, Oracle.WardOf).OrderByDescending(kv => kv.Value[^1]).Take(2))
        {
            var number = int.Parse(ward.Key.Split(' ')[1]);
            await Checks.AreaPage_TablesMatchTheOracle($"wards/{number}", null, number);
        }
    }

    [LiveFact(needsSite: true)]
    public async Task Apis_ForEveryGroupAndTheBusiestAreas()
    {
        await Checks.CrimeApi_MatchesTheOracle("", null, null, null);
        foreach (var group in new[] { "agg-assault", "theft-from-vehicle", "homicide" })
        {
            await Checks.CrimeApi_MatchesTheOracle($"?category={group}", group, null, null);
        }

        var hood = TopHoods(1).First();
        await Checks.CrimeApi_MatchesTheOracle($"?neighborhood={Uri.EscapeDataString(hood)}", null, hood, null);
        await Checks.TrendsApi_MatchesTheOracle("", null, null, null);
        await Checks.TrendsApi_MatchesTheOracle("?category=vehicle-theft", "vehicle-theft", null, null);
    }

    [LiveFact(needsSite: true)]
    public async Task MapApi_PinsAndUnlocatedAddUpToTheSummary()
    {
        await Checks.MapApi_PinsPlusUnlocatedEqualTheSummaryAndTheOracle("?range=30&group=car", "car", 30);
        await Checks.MapApi_PinsPlusUnlocatedEqualTheSummaryAndTheOracle("?range=365", "", 365);
        await Checks.MapApi_PinsPlusUnlocatedEqualTheSummaryAndTheOracle("?range=90&group=theft-from-vehicle", "theft-from-vehicle", 90);
    }

    [LiveFact(needsSite: true)]
    public async Task ResidentFigures_MatchTheReportsTable()
    {
        using var db = LiveWorld.NewContext();
        var visible = await db.Reports.AsNoTracking().Where(r => r.Status == ReportStatus.Active && r.SourceType == SourceType.UserReport).ToListAsync();
        var answered = visible.Where(r => r.PoliceReported != null).ToList();

        var gap = JsonDocument.Parse(await client.GetStringAsync("api/reports/stats/reporting-gap")).RootElement;
        Assert.Equal(answered.Count, gap.GetProperty("respondedCount").GetInt32());
        if (answered.Count > 0)
        {
            Assert.Equal(Math.Round(answered.Count(r => r.PoliceReported == false) / (double)answered.Count * 100, 1), gap.GetProperty("percentUnreported").GetDouble());
        }

        var stats = JsonDocument.Parse(await client.GetStringAsync("api/reports/stats")).RootElement;
        var oracle = LiveWorld.Oracle;
        Assert.Equal(oracle.Sum(Oracle.IsCar, oracle.Through.AddDays(-29), oracle.Through), stats.GetProperty("count").GetInt32());
        // Resident reports are a separate figure and never part of the MPD count.
        var from = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-29);
        Assert.Equal(visible.Count(r => r.ReportedDate >= from), stats.GetProperty("residentReports").GetInt32());
    }
}
