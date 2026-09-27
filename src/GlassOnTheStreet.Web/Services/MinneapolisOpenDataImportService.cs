using System.Text.Json;
using GlassOnTheStreet.Web.Data;
using GlassOnTheStreet.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace GlassOnTheStreet.Web.Services;

/// <summary>
/// Source: City of Minneapolis open data portal, "Crime_Data" feature
/// service (verified 2026-09-25, re-verified 2026-09-27 against a full
/// paginated scan of all ~393,000 rows, not a sample):
/// https://services.arcgis.com/afSMGVsC7QlRK1kZ/arcgis/rest/services/Crime_Data/FeatureServer/0
///
/// We import three categories: "Theft From Motor Vehicle" and "Theft of
/// Motor Vehicle Parts or Accessories" (break-ins; MPD's data doesn't
/// record entry method, so these get IncidentType.Unknown rather than a
/// guessed window-smashed/rifled), and "Motor Vehicle Theft" (the whole
/// car stolen, not broken into -- a different, but definitively known,
/// crime, so it gets its own IncidentType.VehicleStolen rather than being
/// folded into Unknown).
///
/// We deliberately do NOT import "Destruction/Damage/Vandalism of
/// Property", even though it's MPD's largest single category and was
/// imported by an earlier version of this service. That category is not
/// vehicle-specific -- it's MPD's catch-all for any property damage
/// (graffiti, building damage, anything). We checked whether
/// Problem_Initial/Problem_Final (the CAD dispatch-call-type fields) could
/// isolate vehicle-only vandalism within it; they can't. Those fields hold
/// generic dispatch codes ("Damage Property-Rpt Only", "Auto Theft",
/// "Theft") and are cross-contaminated across categories -- rows filed
/// under vandalism have "Auto Theft" as their initial call type and vice
/// versa. There is no field anywhere in this dataset that separates a
/// smashed car window from a spray-painted garage door. Importing that
/// category means importing thousands of non-vehicle incidents onto a car
/// break-in map, which is worse than not having the data at all. This is
/// also why MPD data can never produce a "window smashed" pin: MPD has no
/// field for entry method and no vehicle-specific vandalism category to
/// fall back on either. Every "window smashed" pin has to come from a
/// resident report -- which is the site's actual point.
///
/// There's also a dead clause this replaces: an earlier version matched
/// `Offense LIKE '%Damage to Motor Vehicle%'`, which matches zero rows --
/// no such literal value exists anywhere in the Offense field.
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
    // A full historical backfill of all three categories is ~85k rows
    // before geofence/invalid filtering; 200 pages gives comfortable
    // headroom without being unbounded.
    private const int MaxPages = 200;

    private static readonly TimeZoneInfo CentralTime = ResolveCentralTimeZone();

    // Static (shared across every scoped instance of this service, not
    // per-instance) so the manual admin-triggered import and the daily
    // OfficialDataSyncBackgroundService can never run concurrently. They
    // each build their own in-memory existingCaseNumbers set up front and
    // only call SaveChangesAsync once at the end, so two overlapping runs
    // can both decide the same new case number is new and both try to
    // insert it -- a real race, not hypothetical: caught this exact
    // failure (DbUpdateException, duplicate key on ExternalCaseNumber)
    // when a manual full-history backfill was still running when the
    // background sync's startup timer fired.
    private static readonly SemaphoreSlim ImportLock = new(1, 1);

    public async Task<OfficialImportResult> ImportAsync(int lookbackDays, CancellationToken cancellationToken = default)
    {
        await ImportLock.WaitAsync(cancellationToken);
        try
        {
            return await ImportCoreAsync(lookbackDays, cancellationToken);
        }
        finally
        {
            ImportLock.Release();
        }
    }

    private async Task<OfficialImportResult> ImportCoreAsync(int lookbackDays, CancellationToken cancellationToken)
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
            var where = "(Offense LIKE '%Theft From Motor Vehicle%'" +
                        " OR Offense LIKE '%Theft of Motor Vehicle Parts or Accessories%'" +
                        " OR Offense LIKE '%Motor Vehicle Theft%')" +
                        $" AND Occurred_Date >= TIMESTAMP '{cutoffTimestamp}'";
            var url = "query" +
                       $"?where={Uri.EscapeDataString(where)}" +
                       "&outFields=Case_Number,Address,Occurred_Date,Offense,Neighborhood,Ward,Precinct,wgsXAnon,wgsYAnon" +
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
                var offense = GetString(props, "Offense")?.Trim();
                var incidentType = offense == "Motor Vehicle Theft" ? IncidentType.VehicleStolen : IncidentType.Unknown;

                db.Reports.Add(new Report
                {
                    ReportedDate = reportedDate,
                    DisplayLat = displayLat,
                    DisplayLng = displayLng,
                    IncidentType = incidentType,
                    Offense = offense,
                    Address = GetString(props, "Address")?.Trim(),
                    TimeOfDay = timeOfDay,
                    Neighborhood = GetString(props, "Neighborhood")?.Trim(),
                    Ward = GetInt(props, "Ward"),
                    Precinct = GetInt(props, "Precinct"),
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

    private static int? GetInt(JsonElement props, string name)
    {
        if (!props.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.TryGetInt32(out var i) ? i : null;
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
