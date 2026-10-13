using GlassOnTheStreet.Web.Controllers.Api;
using GlassOnTheStreet.Web.Data;
using GlassOnTheStreet.Web.Models;
using GlassOnTheStreet.Web.Models.Api;
using GlassOnTheStreet.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace GlassOnTheStreet.Tests;

public class CaptchaFailClosedTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static Task<bool> Verify(string environment) =>
        new TurnstileCaptchaService(new HttpClient(), new ConfigurationBuilder().Build(), new Env(environment)).VerifyAsync("token", "1.2.3.4");

    [Fact]
    public async Task WithoutASecretKeyDevelopmentSkipsVerification() => Assert.True(await Verify("Development"));

    [Fact]
    public async Task WithoutASecretKeyProductionRejectsEverySubmission() => Assert.False(await Verify("Production"));
}

public class ReportSubmissionRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 12);

    private static ReportSubmission Valid() => new()
    {
        ReportedDate = Today, Lat = 44.95m, Lng = -93.27m, IncidentType = IncidentType.WindowSmashed
    };

    [Fact]
    public void AcceptsAValidReport() => Assert.Null(ReportSubmissionRules.Validate(Valid(), Today));

    [Fact]
    public void RejectsFutureAndImplausiblyOldDates()
    {
        var future = Valid(); future.ReportedDate = Today.AddDays(1);
        var old = Valid(); old.ReportedDate = new DateOnly(1900, 1, 1);

        Assert.NotNull(ReportSubmissionRules.Validate(future, Today));
        Assert.NotNull(ReportSubmissionRules.Validate(old, Today));
    }

    [Theory]
    [InlineData(IncidentType.Unknown)]
    [InlineData(IncidentType.VehicleStolen)]
    [InlineData((IncidentType)99)]
    public void RejectsTypesTheFormDoesNotOffer(IncidentType type)
    {
        var s = Valid(); s.IncidentType = type;

        Assert.NotNull(ReportSubmissionRules.Validate(s, Today));
    }

    [Fact]
    public void RejectsAnUndefinedTimeOfDay()
    {
        var s = Valid(); s.TimeOfDay = (TimeOfDay)42;

        Assert.NotNull(ReportSubmissionRules.Validate(s, Today));
    }
}

public class FlagThresholdTests
{
    private static GlassOnTheStreetContext NewDb() =>
        new(new DbContextOptionsBuilder<GlassOnTheStreetContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<int> AddReport(GlassOnTheStreetContext db, SourceType source = SourceType.UserReport)
    {
        var report = new Report
        {
            ReportedDate = new DateOnly(2026, 10, 1), DisplayLat = 44.95m, DisplayLng = -93.27m,
            IncidentType = IncidentType.WindowSmashed, Status = ReportStatus.Active, SourceType = source
        };
        db.Reports.Add(report);
        await db.SaveChangesAsync();
        return report.Id;
    }

    [Fact]
    public async Task OneFlagKeepsTheReportVisibleButIsRecorded()
    {
        await using var db = NewDb();
        var id = await AddReport(db);

        await new FlagsApiController(db).Flag(id, new FlagReportRequest { Reason = "spam" }, default);

        Assert.Equal(ReportStatus.Active, (await db.Reports.FindAsync(id))!.Status);
        Assert.Single(db.ReportFlags);
    }

    [Fact]
    public async Task TheThresholdHidesTheReport()
    {
        await using var db = NewDb();
        var id = await AddReport(db);
        var controller = new FlagsApiController(db);

        for (var i = 0; i < FlagsApiController.HideThreshold; i++)
        {
            await controller.Flag(id, null, default);
        }

        Assert.Equal(ReportStatus.Flagged, (await db.Reports.FindAsync(id))!.Status);
    }

    [Fact]
    public async Task ImportedRowsCannotBeFlagged()
    {
        await using var db = NewDb();
        var id = await AddReport(db, SourceType.OfficialImport);

        var result = await new FlagsApiController(db).Flag(id, null, default);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(db.ReportFlags);
    }
}
