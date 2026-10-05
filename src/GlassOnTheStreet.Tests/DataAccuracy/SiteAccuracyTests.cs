using System.Globalization;
using System.Text.Json;
using AngleSharp;
using AngleSharp.Dom;
using GlassOnTheStreet.Web.Models;
using GlassOnTheStreet.Web.Services;

namespace GlassOnTheStreet.Tests.DataAccuracy;

/// <summary>The shared site checks (see <see cref="SiteChecks"/>) run against the synthetic world, plus checks that only make sense there.</summary>
[Collection("world")]
public class SiteAccuracyTests(World world) : IDisposable
{
    private readonly HttpClient client = world.Factory.NewClient();

    private readonly SiteChecks checks = new(world.Factory.NewClient(), world.Oracle);

    private Oracle O => world.Oracle;

    public void Dispose() => client.Dispose();

    [Fact]
    public Task Home_CarThirtyDayFiguresMatchTheOracle() => checks.Home_CarThirtyDayFiguresMatchTheOracle();

    [Fact]
    public Task Home_LoadsTheScriptsThatDrawItsMapAndChartTooltips() => checks.Home_LoadsTheScriptsThatDrawItsMapAndChartTooltips();

    [Fact]
    public Task Home_YearToDateFiguresMatchTheOracleForAllCrimesAndForCarCrime() => checks.Home_YearToDateFiguresMatchTheOracleForAllCrimesAndForCarCrime();

    [Fact]
    public Task Home_CarCrimeIsASubsetOfAllCrimeOnThePage() => checks.Home_CarCrimeIsASubsetOfAllCrimeOnThePage();

    [Fact]
    public Task Home_AllCrimeTypeListShowsTheEightLargestGroupsWithTheirRealCounts() => checks.Home_AllCrimeTypeListShowsTheEightLargestGroupsWithTheirRealCounts();

    [Fact]
    public Task Home_MonthlyBarsMatchTheOracle() => checks.Home_MonthlyBarsMatchTheOracle();

    [Fact]
    public Task Home_TopFiveNeighborhoodsAndWardsMatchTheOracle() => checks.Home_TopFiveNeighborhoodsAndWardsMatchTheOracle();

    [Fact]
    public Task Home_DatesOnThePageAreTheThroughDateAndNeverTheHeldBackDays() => checks.Home_DatesOnThePageAreTheThroughDateAndNeverTheHeldBackDays();

    [Fact]
    public Task Home_EveryNumberSaysWhetherItIsMpdDataOrResidentReportsAndWhichCrimes() => checks.Home_EveryNumberSaysWhetherItIsMpdDataOrResidentReportsAndWhichCrimes();

    [Fact]
    public Task CrimePage_GroupTableAndYearTableMatchTheOracle() => checks.CrimePage_GroupTableAndYearTableMatchTheOracle();

    [Fact]
    public Task CrimePage_MonthlyTableAndAreaTableMatchTheOracle() => checks.CrimePage_MonthlyTableAndAreaTableMatchTheOracle();

    [Fact]
    public Task TrendsPage_CarTablesMatchTheOracle() => checks.TrendsPage_CarTablesMatchTheOracle();

    [Theory]
    [InlineData("neighborhoods/whittier", "Whittier", null)]
    [InlineData("neighborhoods/longfellow", "Longfellow", null)]
    [InlineData("neighborhoods/downtown-west", "Downtown West", null)]
    [InlineData("wards/10", null, 10)]
    [InlineData("wards/6", null, 6)]
    public Task AreaPage_TablesMatchTheOracle(string path, string? hood, int? ward) => checks.AreaPage_TablesMatchTheOracle(path, hood, ward);

    [Theory]
    [InlineData("", null, null, null)]
    [InlineData("?category=agg-assault", "agg-assault", null, null)]
    [InlineData("?category=theft-from-vehicle&neighborhood=Whittier", "theft-from-vehicle", "Whittier", null)]
    [InlineData("?ward=12", null, null, 12)]
    public Task CrimeApi_MatchesTheOracle(string query, string? group, string? hood, int? ward) => checks.CrimeApi_MatchesTheOracle(query, group, hood, ward);

    [Theory]
    [InlineData("", null, null, null)]
    [InlineData("?category=vehicle-theft", "vehicle-theft", null, null)]
    [InlineData("?neighborhood=Longfellow&category=property-damage", "vandalism", "Longfellow", null)]
    public Task TrendsApi_MatchesTheOracle(string query, string? group, string? hood, int? ward) => checks.TrendsApi_MatchesTheOracle(query, group, hood, ward);

    [Fact]
    public Task MonthlyCsv_MatchesTheOracleRowForRow() => checks.MonthlyCsv_MatchesTheOracleRowForRow();

    [Fact]
    public Task AreaCsvs_MatchTheOracle() => checks.AreaCsvs_MatchTheOracle();

    [Fact]
    public Task MonthlyReportPage_QuotesTheOraclesTotalsForItsMonth() => checks.MonthlyReportPage_QuotesTheOraclesTotalsForItsMonth();

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

    private static int N(string? text) => int.Parse(System.Text.RegularExpressions.Regex.Replace(text ?? "", @"[^\d]", ""), CultureInfo.InvariantCulture);

    private static string Text(IElement? e) => System.Text.RegularExpressions.Regex.Replace(e?.TextContent ?? "", @"\s+", " ").Trim();

    private static string Metric(IDocument doc, string key) => Text(doc.QuerySelector($"[data-metric='{key}']"));

    [Fact]
    public async Task Home_ResidentReportingGapIsTheResidentOnlyFigure()
    {
        var doc = await PageAsync("/");

        Assert.Equal($"{Math.Round(4 / 7.0 * 100, 1)}%", Metric(doc, "never-told-police"));
        Assert.Contains("7 answered", Text(doc.QuerySelector("[data-metric='never-told-police']")!.ParentElement!));
    }

    [Fact]
    public async Task Home_CarCategoryRowsKeepMpdAndResidentCountsApart()
    {
        var doc = await PageAsync("/");
        int Row(string key) => N(Text(doc.QuerySelector($"[data-metric='car-category:{key}'] .breakdown-count")));
        var start = new DateOnly(O.Through.Year, 1, 1);

        Assert.Equal(O.Sum(Oracle.Group("theft-from-vehicle"), start, O.Through), Row("Unknown"));
        Assert.Equal(O.Sum(Oracle.Group("vehicle-theft"), start, O.Through), Row("VehicleStolen"));
        Assert.Equal(O.Sum(Oracle.Group("parts-theft"), start, O.Through), Row("PartsTheft"));
        Assert.Equal(O.Sum(Oracle.Group("vandalism"), start, O.Through), Row("PropertyDamage"));
        Assert.Equal(7, Row("WindowSmashed"));
        Assert.Equal(2, Row("Rifled"));
        // Resident rows sit below the divider that says they are counted separately.
        var divider = doc.QuerySelector(".category-divider")!;
        Assert.Contains("not police data", Text(divider));
        Assert.Contains("car-category:WindowSmashed", divider.NextElementSibling!.GetAttribute("data-metric"));
    }

    [Theory]
    [InlineData("?range=7&group=car", "car", 7)]
    [InlineData("?range=30&group=car", "car", 30)]
    [InlineData("?range=90", "", 90)]
    [InlineData("?range=365&group=theft-from-vehicle", "theft-from-vehicle", 365)]
    public Task MapApi_PinsPlusUnlocatedEqualTheSummaryAndTheOracle(string query, string group, int days) =>
        checks.MapApi_PinsPlusUnlocatedEqualTheSummaryAndTheOracle(query, group, days);

    [Fact]
    public async Task WardsApi_ServesAllThirteenWardsWithTheirBoundariesAndCouncilMembers()
    {
        var json = await JsonAsync("/api/wards");
        var features = json.GetProperty("features").EnumerateArray().ToList();
        var service = new WardService();

        Assert.Equal(13, features.Count);
        Assert.Equal(service.RetrievedOn, json.GetProperty("meta").GetProperty("retrievedOn").GetString());
        foreach (var f in features)
        {
            var p = f.GetProperty("properties");
            var ward = service.Find(p.GetProperty("ward").GetInt32())!;
            Assert.Equal(ward.Name, p.GetProperty("name").GetString());
            Assert.Equal(ward.Title, p.GetProperty("title").GetString());
            Assert.Equal(ward.Phone, p.GetProperty("phone").GetString());
            Assert.Equal(ward.StatsUrl, p.GetProperty("statsUrl").GetString());
            Assert.Contains(f.GetProperty("geometry").GetProperty("type").GetString(), new[] { "Polygon", "MultiPolygon" });
            // The label point the map draws "Ward N" at sits inside that ward.
            Assert.True(WardServiceTests.Contains(f.GetProperty("geometry"), p.GetProperty("labelLng").GetDouble(), p.GetProperty("labelLat").GetDouble()));
        }
    }

    [Fact]
    public async Task ReportsStatsApi_KeepsResidentReportsOutOfTheMpdCount()
    {
        var json = await JsonAsync("/api/reports/stats");

        Assert.Equal(O.Sum(Oracle.IsCar, O.Through.AddDays(-29), O.Through), json.GetProperty("count").GetInt32());
        Assert.Equal(9, json.GetProperty("residentReports").GetInt32());

        var gap = await JsonAsync("/api/reports/stats/reporting-gap");
        Assert.Equal(7, gap.GetProperty("respondedCount").GetInt32());
        Assert.Equal(Math.Round(4 / 7.0 * 100, 1), gap.GetProperty("percentUnreported").GetDouble());
    }
}
