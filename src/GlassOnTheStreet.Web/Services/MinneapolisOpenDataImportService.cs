using System.Text.Json;
using GlassOnTheStreet.Web.Data;
using GlassOnTheStreet.Web.Infrastructure;
using GlassOnTheStreet.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace GlassOnTheStreet.Web.Services;

/// <summary>
/// Source: City of Minneapolis open data portal, "Crime_Data" feature
/// service (verified 2026-09-25, re-verified 2026-09-27 against a full
/// paginated scan of all ~393,000 rows, not a sample):
/// https://services.arcgis.com/afSMGVsC7QlRK1kZ/arcgis/rest/services/Crime_Data/FeatureServer/0
///
/// We import four categories, each mapped to its own IncidentType:
/// "Theft From Motor Vehicle" (an actual break-in; MPD's data doesn't
/// record entry method, so this gets IncidentType.Unknown rather than a
/// guessed window-smashed/rifled), "Theft of Motor Vehicle Parts or
/// Accessories" (catalytic converters, wheels, stereos -- doesn't
/// necessarily involve breaking in at all, so it gets its own
/// IncidentType.PartsTheft rather than being conflated with an actual
/// break-in), "Motor Vehicle Theft" (the whole car stolen, not broken
/// into -- a different, but definitively known, crime, so it gets its own
/// IncidentType.VehicleStolen), and "Destruction/Damage/Vandalism of
/// Property" (IncidentType.PropertyDamage -- see below).
///
/// PropertyDamage is a deliberate accuracy tradeoff, not an oversight.
/// "Destruction/Damage/Vandalism of Property" is MPD's catch-all for any
/// property damage -- graffiti, building damage, park benches, anything --
/// not just vehicles. We checked whether Problem_Initial/Problem_Final
/// (the CAD dispatch-call-type fields) could isolate vehicle-only rows
/// within it; they can't. Those fields hold generic dispatch codes
/// ("Damage Property-Rpt Only", "Auto Theft", "Theft") and are
/// cross-contaminated across categories -- rows filed under vandalism have
/// "Auto Theft" as their initial call type and vice versa. There is no
/// field anywhere in this dataset that separates a smashed car window from
/// a spray-painted garage door. An earlier version of this service dropped
/// the category entirely for exactly that reason. It's imported again now
/// at the explicit call of the site's owner, who decided broader coverage
/// is worth more than strict per-pin accuracy here -- but it's kept in its
/// own IncidentType, never folded into Unknown, specifically so the map
/// and its legend can say plainly that a PropertyDamage pin isn't
/// confirmed to be about a vehicle at all. This is also still why MPD data
/// can never produce a genuine "window smashed" pin: MPD has no field for
/// entry method. Every "window smashed" pin has to come from a resident
/// report -- which is the site's actual point.
///
/// There's also a dead clause this replaces: an earlier version matched
/// `Offense LIKE '%Damage to Motor Vehicle%'`, which matches zero rows --
/// no such literal value exists anywhere in the Offense field. Likewise,
/// "Damage to Property" (as opposed to the real category name above) also
/// matches zero rows.
///
/// We read the wgsXAnon/wgsYAnon fields rather than Latitude/Longitude.
/// They are the city's own anonymized point: every incident on a block
/// shares one coordinate, the midpoint of the block ("0048XX 13TH AVE S"
/// sits halfway between 48th St and 49th St, checked against OpenStreetMap
/// cross-street positions). They are stored as-is, with no further
/// snapping, and resident reports (see BlockAnchorService) reuse the same
/// anchors. Note: wgsXAnon/wgsYAnon are Web Mercator (EPSG:3857)
/// meters, not WGS84 degrees, despite the "wgs" name -- see
/// WebMercatorToWgs84 below.
/// </summary>
public class MinneapolisOpenDataImportService(
    HttpClient httpClient,
    GlassOnTheStreetContext db,
    IGeofenceService geofenceService,
    ILogger<MinneapolisOpenDataImportService> logger) : IOfficialDataImportService
{
    private const int PageSize = 1000;
    // A full historical backfill of all four categories is ~133k rows
    // before geofence/invalid filtering (~85k vehicle-specific + ~48k
    // PropertyDamage); 400 pages gives comfortable headroom without being
    // unbounded.
    private const int MaxPages = 400;

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

    public bool IsRunning => ImportLock.CurrentCount == 0;

    public async Task<OfficialImportResult> ImportAsync(int lookbackDays, CancellationToken cancellationToken = default)
    {
        await ImportLock.WaitAsync(cancellationToken);
        try
        {
            return await ImportCoreAsync(DateTime.UtcNow.AddDays(-Math.Abs(lookbackDays)), null, cancellationToken);
        }
        finally
        {
            ImportLock.Release();
        }
    }

    public async Task<OfficialImportResult> ImportRangeAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        // The feed stores Occurred_Date in UTC; the range is in Central dates.
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(from.ToDateTime(TimeOnly.MinValue), CentralTime.Zone);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue), CentralTime.Zone);

        await ImportLock.WaitAsync(cancellationToken);
        try
        {
            return await ImportCoreAsync(startUtc, endUtc, cancellationToken);
        }
        finally
        {
            ImportLock.Release();
        }
    }

    private async Task<OfficialImportResult> ImportCoreAsync(DateTime fromUtc, DateTime? toUtcExclusive, CancellationToken cancellationToken)
    {
        var cutoffMs = new DateTimeOffset(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
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
                        " OR Offense LIKE '%Motor Vehicle Theft%'" +
                        " OR Offense LIKE '%Destruction/Damage/Vandalism of Property%')" +
                        $" AND Occurred_Date >= TIMESTAMP '{cutoffTimestamp}'" +
                        (toUtcExclusive is { } end ? $" AND Occurred_Date < TIMESTAMP '{end:yyyy-MM-dd HH:mm:ss}'" : "");
            var url = "query" +
                       $"?where={Uri.EscapeDataString(where)}" +
                       "&outFields=Case_Number,Address,Occurred_Date,Offense,Neighborhood,Ward,Precinct,wgsXAnon,wgsYAnon," +
                       "Type,DID,Case_NumberAlt,Reported_Date,NIBRS_Crime_Against,NIBRS_Group,NIBRS_Code," +
                       "Offense_Category,Problem_Initial,Problem_Final,Crime_Count" +
                       "&orderByFields=Occurred_Date%20DESC" +
                       $"&resultOffset={offset}" +
                       $"&resultRecordCount={PageSize}" +
                       "&f=geojson";

            // A page that can't be read after retries is a failure, not the end of the
            // data. It used to `break` here and report success, which let a single 504
            // from the city's feed leave the oldest years silently missing.
            var doc = await ArcGisPageFetcher.GetPageAsync(httpClient, url, logger, cancellationToken)
                ?? throw new InvalidOperationException($"MPD feed returned no readable page at offset {offset}; the import is incomplete.");
            var features = doc.GetProperty("features");

            var pageCount = 0;
            var importedThisPage = 0;
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
                var (displayLat, displayLng) = (Math.Round(anonLat, 6), Math.Round(anonLng, 6));
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
                var incidentType = offense switch
                {
                    "Motor Vehicle Theft" => IncidentType.VehicleStolen,
                    "Theft of Motor Vehicle Parts or Accessories" => IncidentType.PartsTheft,
                    "Destruction/Damage/Vandalism of Property" => IncidentType.PropertyDamage,
                    // "Theft From Motor Vehicle" -- an actual break-in, entry
                    // method unrecorded by MPD.
                    _ => IncidentType.Unknown
                };

                var reportedMs = GetLong(props, "Reported_Date");
                var mpdReportedDate = reportedMs is null
                    ? (DateOnly?)null
                    : DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(reportedMs.Value).UtcDateTime);

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
                    Status = ReportStatus.Active,
                    MpdType = GetString(props, "Type")?.Trim(),
                    MpdIncidentId = GetString(props, "DID")?.Trim(),
                    AlternateCaseNumber = GetString(props, "Case_NumberAlt")?.Trim(),
                    MpdReportedDate = mpdReportedDate,
                    NibrsCrimeAgainst = GetString(props, "NIBRS_Crime_Against")?.Trim(),
                    NibrsGroup = GetString(props, "NIBRS_Group")?.Trim(),
                    NibrsCode = GetString(props, "NIBRS_Code")?.Trim(),
                    OffenseCategory = GetString(props, "Offense_Category")?.Trim(),
                    ProblemInitial = GetString(props, "Problem_Initial")?.Trim(),
                    ProblemFinal = GetString(props, "Problem_Final")?.Trim(),
                    CrimeCount = GetInt(props, "Crime_Count")
                });
                imported++;
                importedThisPage++;
            }

            // Saved once per page (up to 1,000 rows) rather than once for
            // the entire run. A full historical backfill can take several
            // minutes across hundreds of pages -- without this, an
            // interruption partway through (app restart, deploy, crash)
            // would discard every row fetched so far, since EF Core only
            // sends inserts to the database on SaveChangesAsync. Saving
            // per page means an interrupted run keeps everything it
            // already fetched; re-running afterward picks up where it
            // left off via the existing case-number dedup.
            if (importedThisPage > 0)
            {
                await db.SaveChangesAsync(cancellationToken);
            }

            if (pageCount < PageSize)
            {
                break;
            }
        }

        logger.LogInformation(
            "Minneapolis open data import: fetched {Fetched}, imported {Imported}, skipped {SkippedDuplicate} duplicate / {SkippedInvalid} invalid",
            fetched, imported, skippedDuplicate, skippedInvalid);

        return new OfficialImportResult(fetched, imported, skippedDuplicate, skippedInvalid);
    }

    private static GlassOnTheStreet.Web.Models.TimeOfDay BucketTimeOfDay(long occurredUnixMs)
    {
        var utc = DateTimeOffset.FromUnixTimeMilliseconds(occurredUnixMs).UtcDateTime;
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, CentralTime.Zone);
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
