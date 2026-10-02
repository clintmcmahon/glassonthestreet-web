using GlassOnTheStreet.Web.Services;
using Xunit;

namespace GlassOnTheStreet.Tests;

public class MonthlyReportTests
{
    private static IncidentRow Row(int id, string date, string group, string? hood = "Alpha", short count = 1) =>
        new(id, DateOnly.Parse(date), 12, (byte)CrimeGroups.IndexOf(group), count, hood, 1, 44.95f, -93.25f);

    private static MonthlyReportService Service(IncidentRow[] rows, string through) =>
        new(new FakeIncidentCache(rows, DateOnly.Parse(through)));

    [Fact]
    public async Task OnlyMonthsThatHaveEndedInTheDataAreAvailable()
    {
        var service = Service([Row(1, "2026-08-10", "burglary")], "2026-09-15");

        Assert.NotNull(await service.GetAsync(2026, 8));
        Assert.Null(await service.GetAsync(2026, 9)); // September hasn't ended
        Assert.Null(await service.GetAsync(2018, 12)); // before the data starts
    }

    [Fact]
    public async Task Report_ComparesWithThePriorMonthAndTheSameMonthLastYearAndSkipsNonCrimes()
    {
        var rows = new[]
        {
            Row(1, "2026-08-01", "burglary", count: 10),
            Row(2, "2026-08-02", "shots-fired", count: 99), // not a crime
            Row(3, "2026-07-01", "burglary", count: 8),
            Row(4, "2025-08-01", "burglary", count: 5),
        };

        var report = await Service(rows, "2026-09-30").GetAsync(2026, 8);

        Assert.NotNull(report);
        Assert.Equal(10, report!.Total);
        Assert.Equal(8, report.PriorMonthTotal);
        Assert.Equal(5, report.LastYearTotal);
        Assert.Equal(13, report.Trend.Length);
        Assert.Equal(10, report.Trend[^1]);
        Assert.Contains("10 offenses in August 2026", report.Narrative[0]);
        Assert.Contains("up 25% from July 2026", report.Narrative[0]);
        Assert.Contains("up 100% from August 2025", report.Narrative[0]);
    }

    [Fact]
    public async Task Report_IsProvisionalUntilAWeekAfterTheMonthEnds()
    {
        var rows = new[] { Row(1, "2026-08-01", "burglary") };

        Assert.True((await Service(rows, "2026-09-03").GetAsync(2026, 8))!.IsProvisional);
        Assert.False((await Service(rows, "2026-09-30").GetAsync(2026, 8))!.IsProvisional);
    }

    [Fact]
    public async Task Movers_NeedEnoughVolumeToBeNamed()
    {
        var rows = new[]
        {
            Row(1, "2026-08-01", "burglary", hood: "Big", count: 60),
            Row(2, "2025-08-01", "burglary", hood: "Big", count: 30),
            Row(3, "2026-08-01", "burglary", hood: "Tiny", count: 3),
            Row(4, "2025-08-01", "burglary", hood: "Tiny", count: 1),
        };

        var report = await Service(rows, "2026-09-30").GetAsync(2026, 8);

        Assert.Equal(["Big"], report!.Increases.Select(m => m.Name).ToArray());
    }

    [Fact]
    public async Task List_IsNewestFirstAndCoversFinishedMonthsOnly()
    {
        var list = await Service([Row(1, "2026-01-05", "burglary")], "2026-03-15").ListAsync();

        Assert.Equal("February 2026", list[0].Label);
        Assert.Equal("January 2026", list[1].Label);
        Assert.DoesNotContain(list, m => m.Month == 3 && m.Year == 2026);
    }
}

public class OgImageTests
{
    [Fact]
    public void Render_ProducesA1200x630Png()
    {
        var png = new OgImageService().Render(new OgCard(
            "Crime in Steven's Square - Loring Heights, Minneapolis", "35,008", "offenses so far in 2026",
            "up 4% from 2025, up 8% from 2019", "glassonthestreet.com  |  MPD data through Sep 30, 2026"));

        Assert.True(png.Length > 5_000);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
        // PNG header: width and height are big-endian ints at offsets 16 and 20.
        Assert.Equal(1200, (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19]);
        Assert.Equal(630, (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]);
    }

    [Fact]
    public void Render_CopesWithVeryLongTitlesAndNumbers()
    {
        var png = new OgImageService().Render(new OgCard(new string('x', 400), "9,999,999,999", "offenses", new string('y', 400), "footer"));

        Assert.True(png.Length > 1_000);
    }
}
