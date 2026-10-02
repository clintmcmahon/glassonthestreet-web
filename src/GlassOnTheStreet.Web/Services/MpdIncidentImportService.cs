using System.Text.Json;
using GlassOnTheStreet.Web.Data;
using GlassOnTheStreet.Web.Infrastructure;
using GlassOnTheStreet.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace GlassOnTheStreet.Web.Services;

/// <summary>
/// Imports the full MPD crime feed (every offense, not just the four
/// car-related categories <see cref="MinneapolisOpenDataImportService"/>
/// maps onto the map) from the City of Minneapolis "Crime_Data" feature
/// service into <see cref="MpdIncident"/>.
/// </summary>
public class MpdIncidentImportService(
    HttpClient httpClient,
    GlassOnTheStreetContext db,
    ILogger<MpdIncidentImportService> logger) : IMpdIncidentImportService
{
    private const int PageSize = 1000;

    // 60k rows a year is about 60 pages; this is a ceiling against a runaway loop.
    private const int MaxPages = 400;

    private static readonly SemaphoreSlim ImportLock = new(1, 1);

    public bool IsRunning => ImportLock.CurrentCount == 0;

    public async Task<OfficialImportResult> ImportRangeAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        await ImportLock.WaitAsync(cancellationToken);
        try
        {
            return await ImportCoreAsync(from, to, cancellationToken);
        }
        finally
        {
            ImportLock.Release();
        }
    }

    private async Task<OfficialImportResult> ImportCoreAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        // The feed stores Occurred_Date in UTC; the range is in Central dates.
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(from.ToDateTime(TimeOnly.MinValue), CentralTime.Zone);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue), CentralTime.Zone);
        var where = $"Occurred_Date >= TIMESTAMP '{startUtc:yyyy-MM-dd HH:mm:ss}' AND Occurred_Date < TIMESTAMP '{endUtc:yyyy-MM-dd HH:mm:ss}'";

        // A one-day pad: the stored date is Central, the feed's cutoffs are UTC.
        var existing = await db.MpdIncidents
            .Where(i => i.OccurredDate >= from.AddDays(-1) && i.OccurredDate <= to.AddDays(1))
            .Select(i => i.ExternalKey)
            .ToHashSetAsync(cancellationToken);

        var fetched = 0;
        var imported = 0;
        var skippedDuplicate = 0;
        var skippedInvalid = 0;

        for (var page = 0; page < MaxPages; page++)
        {
            var url = "query" +
                      $"?where={Uri.EscapeDataString(where)}" +
                      "&outFields=Case_Number,Address,Occurred_Date,Offense,Offense_Category,Type,Neighborhood,Ward,Precinct," +
                      "wgsXAnon,wgsYAnon,NIBRS_Code,NIBRS_Crime_Against,Crime_Count" +
                      "&returnGeometry=false" +
                      "&orderByFields=Occurred_Date%20ASC,OBJECTID%20ASC" +
                      $"&resultOffset={page * PageSize}" +
                      $"&resultRecordCount={PageSize}" +
                      "&f=json";

            // An unreadable page is a failure, not the end of the data: the caller
            // must not mark this range complete.
            var doc = await ArcGisPageFetcher.GetPageAsync(httpClient, url, logger, cancellationToken)
                ?? throw new InvalidOperationException($"MPD crime feed returned no readable page at offset {page * PageSize}.");
            var features = doc.GetProperty("features");

            var pageCount = 0;
            var batch = new List<MpdIncident>();
            foreach (var feature in features.EnumerateArray())
            {
                pageCount++;
                fetched++;

                if (!feature.TryGetProperty("attributes", out var attrs) || MpdIncidentMapper.FromAttributes(attrs) is not { } incident)
                {
                    skippedInvalid++;
                    continue;
                }

                if (!existing.Add(incident.ExternalKey))
                {
                    skippedDuplicate++;
                    continue;
                }

                batch.Add(incident);
            }

            if (batch.Count > 0)
            {
                db.MpdIncidents.AddRange(batch);
                await db.SaveChangesAsync(cancellationToken);
                imported += batch.Count;
                db.ChangeTracker.Clear();
            }

            if (pageCount < PageSize)
            {
                break;
            }
        }

        logger.LogInformation(
            "MPD crime feed {From} to {To}: fetched {Fetched}, imported {Imported}, skipped {SkippedDuplicate} duplicate / {SkippedInvalid} invalid",
            from, to, fetched, imported, skippedDuplicate, skippedInvalid);

        return new OfficialImportResult(fetched, imported, skippedDuplicate, skippedInvalid);
    }

}
