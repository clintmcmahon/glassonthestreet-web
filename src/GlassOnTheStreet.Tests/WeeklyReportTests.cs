using GlassOnTheStreet.Web.Services;
using Xunit;

namespace GlassOnTheStreet.Tests;

public class WeeklyReportTests
{
    // 2026-09-28 is a Monday.
    private static readonly DateOnly Week = new(2026, 9, 28);

    private static IncidentRow Row(int id, DateOnly date, string group, short count = 1, byte hour = 12, byte ward = 1, int address = 0) =>
        new(id, date, hour, (byte)CrimeGroups.IndexOf(group), count, "Alpha", ward, 44.95f, -93.25f, address);

    private static WeeklyReportService Service(IncidentRow[] rows, string through, string[]? addresses = null) =>
        new(new FakeIncidentCache(rows, DateOnly.Parse(through), addresses));

    [Fact]
    public async Task OnlyFinishedMondayWeeksWithAYearOfHistoryAreAvailable()
    {
        var service = Service([Row(1, Week, "burglary")], "2026-10-04");

        Assert.NotNull(await service.GetAsync(Week));
        Assert.Null(await service.GetAsync(Week.AddDays(7))); // not finished
        Assert.Null(await service.GetAsync(Week.AddDays(1))); // not a Monday
        Assert.Null(await service.GetAsync(new DateOnly(2019, 3, 4))); // no prior year to compare
    }

    [Fact]
    public async Task Report_ComparesWithThePriorFourWeeksAndTheSameWeekLastYearAndSkipsNonCrimes()
    {
        var rows = new List<IncidentRow>
        {
            Row(1, Week, "burglary", count: 10),
            Row(2, Week, "shots-fired", count: 99), // not a crime
            Row(3, Week.AddDays(-364), "burglary", count: 5),
        };
        for (var i = 1; i <= 4; i++)
        {
            rows.Add(Row(10 + i, Week.AddDays(-7 * i), "burglary", count: 8));
        }

        var report = await Service([.. rows], "2026-10-04").GetAsync(Week);

        Assert.NotNull(report);
        Assert.Equal(10, report!.Total);
        Assert.Equal(8, report.Baseline);
        Assert.Equal(5, report.LastYearTotal);
        Assert.Equal(13, report.Trend.Length);
        Assert.Equal(10, report.Trend[^1]);
        Assert.Contains("10 offenses for the week of September 28 to October 4, 2026", report.Narrative[0]);
        Assert.Contains("up 25% from the 4-week average of 8", report.Narrative[0]);
        Assert.Contains("up 100% from the same week a year earlier (5)", report.Narrative[0]);
    }

    [Fact]
    public async Task Spike_NeedsVolumeAndAClearJumpOverTheEightWeekRange()
    {
        var rows = new List<IncidentRow> { Row(1, Week, "robbery", count: 30), Row(2, Week, "arson", count: 5) };
        for (var i = 1; i <= 8; i++)
        {
            rows.Add(Row(100 + i, Week.AddDays(-7 * i), "robbery", count: (short)(10 + i % 2)));
            rows.Add(Row(200 + i, Week.AddDays(-7 * i), "arson", count: (short)(i % 2))); // quiet type, small counts
        }

        var report = await Service([.. rows], "2026-10-04").GetAsync(Week);

        Assert.Equal(["robbery"], report!.Spikes.Select(s => s.Key).ToArray());
    }

    [Fact]
    public async Task Buckets_AndWards_CountTheWeekOnly()
    {
        var rows = new[]
        {
            Row(1, Week, "burglary", hour: 2, ward: 3),                  // Monday overnight
            Row(2, Week.AddDays(6), "burglary", hour: 23, ward: 3),      // Sunday evening
            Row(3, Week.AddDays(-1), "burglary", hour: 2, ward: 3),      // prior Sunday, outside the week
        };

        var report = await Service(rows, "2026-10-04").GetAsync(Week);

        Assert.Equal(1, report!.ByDay[0].Count);
        Assert.Equal(1, report.ByDay[6].Count);
        Assert.Equal(1, report.ByTimeOfDay[0].Count);
        Assert.Equal(1, report.ByTimeOfDay[3].Count);
        Assert.Equal(2, report.Wards.Single(w => w.Ward == 3).Count);
    }

    [Fact]
    public async Task RepeatBlocks_NeedThreeCarOffensesOnOneBlock()
    {
        string[] addresses = ["", "0048XX 13TH AVE S", "0010XX MAIN ST"];
        var rows = new[]
        {
            Row(1, Week, "theft-from-vehicle", count: 2, address: 1),
            Row(2, Week.AddDays(1), "vehicle-theft", address: 1),
            Row(3, Week, "theft-from-vehicle", count: 2, address: 2),
            Row(4, Week, "burglary", count: 5, address: 2), // not a car offense
        };

        var report = await Service(rows, "2026-10-04", addresses).GetAsync(Week);

        var block = Assert.Single(report!.RepeatBlocks);
        Assert.Equal("0048XX 13TH AVE S", block.Address);
        Assert.Equal(3, block.Count);
    }

    [Fact]
    public async Task List_IsNewestFirst()
    {
        var list = await Service([Row(1, Week, "burglary")], "2026-10-11").ListAsync(3);

        Assert.Equal(Week.AddDays(7), list[0].Start);
        Assert.Equal(Week, list[1].Start);
    }
}
