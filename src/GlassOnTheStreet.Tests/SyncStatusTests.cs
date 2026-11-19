using System.Net;
using System.Text;
using System.Text.Json;
using GlassOnTheStreet.Web.Data;
using GlassOnTheStreet.Web.Models;
using GlassOnTheStreet.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GlassOnTheStreet.Tests;

public class SyncStatusServiceTests
{
    private static GlassOnTheStreetContext NewDb() =>
        new(new DbContextOptionsBuilder<GlassOnTheStreetContext>().UseInMemoryDatabase($"sync-{Guid.NewGuid():N}").Options);

    [Fact]
    public async Task AStepIsRecordedWithItsStartTimeAndClearedAtTheEnd()
    {
        await using var db = NewDb();
        var service = new SyncStatusService(db);

        await service.RecordStepAsync("Daily import, all offenses (last 30 days)");
        var running = await service.GetStatusAsync();
        Assert.Equal("Daily import, all offenses (last 30 days)", running!.CurrentStep);
        Assert.NotNull(running.CurrentStepStartedAt);

        await service.RecordStepAsync(null);
        var done = await service.GetStatusAsync();
        Assert.Null(done!.CurrentStep);
        Assert.Null(done.CurrentStepStartedAt);
    }

    [Fact]
    public async Task AStepBeforeAnySuccessDoesNotReadAsASync()
    {
        await using var db = NewDb();
        var service = new SyncStatusService(db);

        await service.RecordStepAsync("History import");

        Assert.Null(await service.GetLastSyncedAtAsync());
    }

    [Fact]
    public async Task ASyncCanKeepAFailureFromAnotherStepOfTheSameRun()
    {
        await using var db = NewDb();
        var service = new SyncStatusService(db);

        await service.RecordFailureAsync("History import: MySqlException: Duplicate entry");
        await service.RecordSyncAsync(clearFailure: false);
        Assert.NotNull(await service.GetLastSyncedAtAsync());
        Assert.Equal("History import: MySqlException: Duplicate entry", (await service.GetLastFailureAsync())!.Error);

        await service.RecordSyncAsync();
        Assert.Null(await service.GetLastFailureAsync());
    }

    [Fact]
    public async Task TheFeedEditTimeIsStored()
    {
        await using var db = NewDb();
        var service = new SyncStatusService(db);
        var edited = new DateTime(2026, 11, 19, 0, 9, 59, DateTimeKind.Utc);

        await service.RecordFeedEditedAsync(edited);

        Assert.Equal(edited, (await service.GetStatusAsync())!.FeedLastEditedAt);
    }
}

public class SyncFailureMessageTests
{
    [Fact]
    public void TheUnderlyingErrorIsShownNotEfCoresWrapper()
    {
        var ex = new DbUpdateException(
            "An error occurred while saving the entity changes. See the inner exception for details.",
            new InvalidOperationException("Duplicate entry '26-1|290|Vandalism' for key 'IX_MpdIncidents_ExternalKey'"));

        var message = OfficialDataSyncBackgroundService.FailureMessage(ex);

        Assert.Contains("Duplicate entry", message);
        Assert.Contains("DbUpdateException", message);
        Assert.DoesNotContain("See the inner exception", message);
    }

    [Fact]
    public void APlainExceptionKeepsItsOwnMessage()
    {
        var message = OfficialDataSyncBackgroundService.FailureMessage(new InvalidOperationException("MPD crime feed returned no readable page at offset 2000."));

        Assert.Equal("InvalidOperationException: MPD crime feed returned no readable page at offset 2000.", message);
    }
}

public class FeedLastEditedTests
{
    [Fact]
    public void ReadsDataLastEditDateFromTheLayerDescription()
    {
        var layer = JsonDocument.Parse("""{"editingInfo":{"lastEditDate":1795097194439,"schemaLastEditDate":1795097194439,"dataLastEditDate":1795097194439}}""").RootElement;

        Assert.Equal(new DateTime(2026, 11, 19, 14, 6, 34, 439, DateTimeKind.Utc), MpdIncidentImportService.FeedLastEdited(layer));
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"editingInfo":{}}""")]
    [InlineData("""{"editingInfo":{"dataLastEditDate":null}}""")]
    [InlineData("""{"error":{"code":500}}""")]
    public void IsNullWhenTheLayerDoesNotSay(string json) =>
        Assert.Null(MpdIncidentImportService.FeedLastEdited(JsonDocument.Parse(json).RootElement));
}

public class IncidentImportKeyCollisionTests
{
    private sealed class OneRowFeed(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    [Fact]
    public async Task ARowAlreadyOnFileUnderAnOlderDateIsNotInsertedAgain()
    {
        // The city re-dated case 26-100 from September into November. The stored copy is outside the import's range, so
        // only a table-wide check stops a second insert (which the unique index on ExternalKey would reject in MySQL).
        await using var db = new GlassOnTheStreetContext(
            new DbContextOptionsBuilder<GlassOnTheStreetContext>().UseInMemoryDatabase($"collide-{Guid.NewGuid():N}").Options);
        db.MpdIncidents.Add(new MpdIncident
        {
            ExternalKey = "26-100|290|Destruction/Damage/Vandalism of Property",
            CaseNumber = "26-100",
            OccurredDate = new DateOnly(2026, 9, 2),
            GroupKey = "vandalism",
            Offense = "Destruction/Damage/Vandalism of Property",
            CrimeCount = 1
        });
        await db.SaveChangesAsync();

        var occurred = new DateTimeOffset(2026, 11, 10, 18, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var feed = JsonSerializer.Serialize(new
        {
            features = new[]
            {
                new { attributes = new Dictionary<string, object> { ["Case_Number"] = "26-100", ["Offense"] = "Destruction/Damage/Vandalism of Property", ["NIBRS_Code"] = "290", ["Occurred_Date"] = occurred, ["Crime_Count"] = "1" } },
                new { attributes = new Dictionary<string, object> { ["Case_Number"] = "26-200", ["Offense"] = "Motor Vehicle Theft", ["NIBRS_Code"] = "240", ["Occurred_Date"] = occurred, ["Crime_Count"] = "1" } }
            }
        });
        var importer = new MpdIncidentImportService(
            new HttpClient(new OneRowFeed(feed)) { BaseAddress = new Uri("https://feed.test/") }, db, NullLogger<MpdIncidentImportService>.Instance);

        var result = await importer.ImportRangeAsync(new DateOnly(2026, 10, 20), new DateOnly(2026, 11, 19));

        Assert.Equal(1, result.Imported);
        Assert.Equal(1, result.SkippedDuplicate);
        Assert.Equal(2, await db.MpdIncidents.CountAsync());
    }
}
