using System.Text.Json;
using GlassOnTheStreet.Web.Data;
using GlassOnTheStreet.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace GlassOnTheStreet.Web.Services;

/// <summary>
/// Source: City of Minneapolis open data portal, "Crime_Data" feature
/// service (verified 2026-09-25):
/// https://services.arcgis.com/afSMGVsC7QlRK1kZ/arcgis/rest/services/Crime_Data/FeatureServer/0
///
/// We only import the "Theft From Motor Vehicle" offense category. MPD's
/// data doesn't record entry method, so every imported row gets
/// IncidentType.Unknown rather than guessing window-smashed vs. rifled.
///
/// We read the wgsXAnon/wgsYAnon fields rather than Latitude/Longitude.
/// The "Anon" naming suggests the city intends these as an anonymized
/// coordinate, but spot-checking several theft-from-vehicle records shows
/// they're effectively identical to the precise point for this offense
/// category -- whatever jittering the city applies elsewhere doesn't seem
/// to apply here. We treat them as precise and run our own
/// LocationPrivacyService.SnapToBlock on top regardless, same as any
/// resident-submitted report. Note also: wgsXAnon/wgsYAnon are Web
/// Mercator (EPSG:3857) meters, not WGS84 degrees, despite the "wgs"
/// name -- see WebMercatorToWgs84 below.
/// </summary>
public class MinneapolisOpenDataImportService(
    HttpClient httpClient,
    GlassOnTheStreetContext db,
    ILocationPrivacyService privacyService,
    IGeofenceService geofenceService,
    ILogger<MinneapolisOpenDataImportService> logger) : IOfficialDataImportService
{
    private const int PageSize = 1000;
    private const int MaxPages = 20; // safety valve -- 20k rows is far more than a launch seed needs

    private static readonly TimeZoneInfo CentralTime = ResolveCentralTimeZone();

    public async Task<OfficialImportResult> ImportAsync(int lookbackDays, CancellationToken cancellationToken = default)
    {
        var cutoffMs = DateTimeOffset.UtcNow.AddDays(-Math.Abs(lookbackDays)).ToUnixTimeMilliseconds();
        var existingCaseNumbers = await db.Reports
            .Where(r => r.ExternalCaseNumber != null)
            .Select(r => r.ExternalCaseNumber!)
            .ToHashSetAsync(cancellationToken);

        var fetched = 0;
        var imported = 0;
        var skippedDuplicate = 0;
        var skippedInvalid = 0;

        // This feature service rejects a bare epoch-millis comparison against
        // a date field ("Unable to perform query"); it wants a TIMESTAMP
        // literal instead. Occurred_Date is stored/compared in UTC.
        var cutoffTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(cutoffMs).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss");

        for (var page = 0; page < MaxPages; page++)
        {
            var offset = page * PageSize;
            var where = $"(Offense LIKE '%Theft From Motor Vehicle%' OR Offense LIKE '%Damage to Motor Vehicle%' OR Offense LIKE '%Destruction/Damage/Vandalism of Property%') AND Occurred_Date >= TIMESTAMP '{cutoffTimestamp}'";            
            var url = "query" +
                       $"?where={Uri.EscapeDataString(where)}" +
                       "&outFields=Case_Number,Occurred_Date,Offense,Neighborhood,wgsXAnon,wgsYAnon" +
                       "&orderByFields=Occurred_Date%20DESC" +
                       $"&resultOffset={offset}" +
                       $"&resultRecordCount={PageSize}" +
                       "&f=geojson";

            using var response = await httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Minneapolis open data request failed with {StatusCode}", response.StatusCode);
                break;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var doc = await JsonSerializer.DeserializeAsync<JsonElement>(stream, cancellationToken: cancellationToken);

            if (!doc.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var pageCount = 0;
            foreach (var feature in features.EnumerateArray())
            {
                pageCount++;
                fetched++;

                if (!feature.TryGetProperty("properties", out var props))
                {
                    skippedInvalid++;
                    continue;
                }

                var caseNumber = GetString(props, "Case_Number")?.Trim();
                if (string.IsNullOrEmpty(caseNumber))
                {
                    skippedInvalid++;
                    continue;
                }

                if (!existingCaseNumbers.Add(caseNumber))
                {
                    skippedDuplicate++;
                    continue;
                }

                // Despite the name, wgsXAnon/wgsYAnon are Web Mercator
                // (EPSG:3857) meters, not WGS84 degrees -- confirmed against
                // this service's own geometry output, which is in degrees
                // and several orders of magnitude smaller. Convert before
                // treating these as a lat/lng pair.
                var mercatorX = GetDecimal(props, "wgsXAnon");
                var mercatorY = GetDecimal(props, "wgsYAnon");
                if (mercatorX is null || mercatorY is null)
                {
                    skippedInvalid++;
                    continue;
                }

                var (anonLat, anonLng) = WebMercatorToWgs84(mercatorX.Value, mercatorY.Value);
                var (displayLat, displayLng) = privacyService.SnapToBlock(anonLat, anonLng);
                if (!geofenceService.IsWithinServiceArea(displayLat, displayLng))
                {
                    skippedInvalid++;
                    continue;
                }

                var occurredMs = GetLong(props, "Occurred_Date");
                var reportedDate = occurredMs is null
                    ? DateOnly.FromDateTime(DateTime.UtcNow)
                    : DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(occurredMs.Value).UtcDateTime);
                var timeOfDay = occurredMs is null ? (TimeOfDay?)null : BucketTimeOfDay(occurredMs.Value);

                db.Reports.Add(new Report
                {
                    ReportedDate = reportedDate,
                    DisplayLat = displayLat,
                    DisplayLng = displayLng,
                    IncidentType = IncidentType.Unknown,
                    Offense = GetString(props, "Offense")?.Trim(),
                    TimeOfDay = timeOfDay,
                    Neighborhood = GetString(props, "Neighborhood")?.Trim(),
                    SourceType = SourceType.OfficialImport,
                    ExternalCaseNumber = caseNumber,
                    Status = ReportStatus.Active
                });
                imported++;
            }

            if (pageCount < PageSize)
            {
                break;
            }
        }

        if (imported > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation(
            "Minneapolis open data import: fetched {Fetched}, imported {Imported}, skipped {SkippedDuplicate} duplicate / {SkippedInvalid} invalid",
            fetched, imported, skippedDuplicate, skippedInvalid);

        return new OfficialImportResult(fetched, imported, skippedDuplicate, skippedInvalid);
    }

    private static GlassOnTheStreet.Web.Models.TimeOfDay BucketTimeOfDay(long occurredUnixMs)
    {
        var utc = DateTimeOffset.FromUnixTimeMilliseconds(occurredUnixMs).UtcDateTime;
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, CentralTime);
        return local.Hour switch
        {
            >= 0 and < 6 => GlassOnTheStreet.Web.Models.TimeOfDay.Overnight,
            >= 6 and < 12 => GlassOnTheStreet.Web.Models.TimeOfDay.Morning,
            >= 12 and < 17 => GlassOnTheStreet.Web.Models.TimeOfDay.Afternoon,
            >= 17 and < 22 => GlassOnTheStreet.Web.Models.TimeOfDay.Evening,
            _ => GlassOnTheStreet.Web.Models.TimeOfDay.Overnight
        };
    }

    private const double MercatorRadius = 20037508.34;

    private static (decimal Lat, decimal Lng) WebMercatorToWgs84(decimal x, decimal y)
    {
        var lng = (double)x / MercatorRadius * 180.0;
        var lat = 180.0 / Math.PI * (2 * Math.Atan(Math.Exp((double)y / MercatorRadius * Math.PI)) - Math.PI / 2);
        return ((decimal)lat, (decimal)lng);
    }

    private static TimeZoneInfo ResolveCentralTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
        }
    }

    private static string? GetString(JsonElement props, string name) =>
        props.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static decimal? GetDecimal(JsonElement props, string name)
    {
        if (!props.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.TryGetDecimal(out var d) ? d : null;
    }

    private static long? GetLong(JsonElement props, string name)
    {
        if (!props.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.TryGetInt64(out var l) ? l : null;
    }
}
