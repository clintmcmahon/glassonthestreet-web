using GlassOnTheStreet.Web.Services;
using Xunit;

namespace GlassOnTheStreet.Tests;

public class YearEndProjectionTests
{
    [Fact]
    public void ProjectYearEnd_DividesYearToDateByShareAlreadyDone()
    {
        // Earlier years were 73% done by this date: 7,300 so far -> 10,000.
        Assert.Equal(10_000, ReportStatsService.ProjectYearEnd(7_300, 7_300, 10_000));
    }

    [Fact]
    public void ProjectYearEnd_EqualsActualWhenTheYearIsOver()
    {
        Assert.Equal(9_000, ReportStatsService.ProjectYearEnd(9_000, 5_000, 5_000));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 0)]
    public void ProjectYearEnd_IsNullWithoutPriorData(int priorToDate, int priorFull)
    {
        Assert.Null(ReportStatsService.ProjectYearEnd(500, priorToDate, priorFull));
    }
}
