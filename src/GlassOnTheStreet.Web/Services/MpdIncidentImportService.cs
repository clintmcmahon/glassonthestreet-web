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
        var occurrences = new Dictionary<string, int>();

        for (var page = 0; page < MaxPages; page++)
        {
            var url = RowsUrl(where, page);

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

                incident.ExternalKey = UniqueKey(incident.ExternalKey, occurrences);

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

    // ---------------------------------------------------------------- shared

    private static string WhereForRange(DateOnly from, DateOnly to)
    {
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(from.ToDateTime(TimeOnly.MinValue), CentralTime.Zone);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue), CentralTime.Zone);
        return $"Occurred_Date >= TIMESTAMP '{startUtc:yyyy-MM-dd HH:mm:ss}' AND Occurred_Date < TIMESTAMP '{endUtc:yyyy-MM-dd HH:mm:ss}'";
    }

    private static string RowsUrl(string where, int page) =>
        "query" +
        $"?where={Uri.EscapeDataString(where)}" +
        "&outFields=Case_Number,Address,Occurred_Date,Offense,Offense_Category,Type,Neighborhood,Ward,Precinct," +
        "wgsXAnon,wgsYAnon,NIBRS_Code,NIBRS_Crime_Against,Crime_Count" +
        "&returnGeometry=false" +
        "&orderByFields=Occurred_Date%20ASC,OBJECTID%20ASC" +
        $"&resultOffset={page * PageSize}" +
        $"&resultRecordCount={PageSize}" +
        "&f=json";

    private const int KeyLength = 160;

    /// <summary>
    /// The feed can list one case's same offense more than once (two weapons offenses in one incident, two ShotSpotter
    /// activations); the city's own totals count each row. The first keeps the plain key, later ones get "#2", "#3",
    /// in the feed's order, so none is mistaken for a repeat of the first.
    /// </summary>
    private static string UniqueKey(string baseKey, Dictionary<string, int> occurrences)
    {
        var n = occurrences[baseKey] = occurrences.GetValueOrDefault(baseKey) + 1;
        if (n == 1)
        {
            return baseKey;
        }

        var suffix = $"#{n}";
        return (baseKey.Length + suffix.Length <= KeyLength ? baseKey : baseKey[..(KeyLength - suffix.Length)]) + suffix;
    }

    // ---------------------------------------------------------------- reconcile

    private const double MaxDeleteShare = 0.05;

    private const int MinDeleteAllowance = 25;

    public async Task<ReconcileResult> ReconcileMonthsAsync(DateOnly firstMonth, DateOnly lastMonth, CancellationToken cancellationToken = default)
    {
        await ImportLock.WaitAsync(cancellationToken);
        try
        {
            var checkedMonths = 0;
            var changed = 0;
            var inserted = 0;
            var updated = 0;
            var deleted = 0;
            var skipped = 0;

            for (var month = new DateOnly(firstMonth.Year, firstMonth.Month, 1); month <= lastMonth; month = month.AddMonths(1))
            {
                var end = month.AddMonths(1).AddDays(-1);
                var source = await SourceFingerprintAsync(month, end, cancellationToken);
                var ours = await StoredFingerprintAsync(month, end, cancellationToken);
                checkedMonths++;
                if (SameFingerprint(source, ours))
                {
                    continue;
                }

                var result = await ReconcileRangeAsync(month, end, cancellationToken);
                changed++;
                inserted += result.Inserted;
                updated += result.Updated;
                deleted += result.Deleted;
                skipped += result.DeletesSkipped;
                logger.LogWarning(
                    "Reconciled {Month:yyyy-MM}: feed had {SourceRows} rows / {SourceOffenses} offenses, database had {OurRows} / {OurOffenses}; added {Inserted}, updated {Updated}, removed {Deleted}, kept {Skipped}",
                    month, source.GetValueOrDefault("t:").Rows, source.GetValueOrDefault("t:").Offenses, ours.GetValueOrDefault("t:").Rows, ours.GetValueOrDefault("t:").Offenses, result.Inserted, result.Updated, result.Deleted, result.DeletesSkipped);
            }

            return new ReconcileResult(checkedMonths, changed, inserted, updated, deleted, skipped);
        }
        finally
        {
            ImportLock.Release();
        }
    }

    /// <summary>
    /// A month's fingerprint: its row and offense totals, and the same totals by offense type, neighborhood and ward.
    /// Totals alone miss a revision that moves an offense between types or places without changing how many there are.
    /// </summary>
    private async Task<Dictionary<string, (int Rows, int Offenses)>> SourceFingerprintAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var fingerprint = new Dictionary<string, (int, int)>();
        foreach (var (prefix, fields) in new[] { ("t", ""), ("o", "Offense_Category,Offense"), ("n", "Neighborhood"), ("w", "Ward") })
        {
            var url = "query" +
                      $"?where={Uri.EscapeDataString(WhereForRange(from, to))}" +
                      "&outStatistics=" + Uri.EscapeDataString("""[{"statisticType":"count","onStatisticField":"OBJECTID","outStatisticFieldName":"n"},{"statisticType":"sum","onStatisticField":"Crime_Count","outStatisticFieldName":"s"}]""") +
                      (fields.Length == 0 ? "" : $"&groupByFieldsForStatistics={fields}") +
                      "&f=json";
            var doc = await ArcGisPageFetcher.GetPageAsync(httpClient, url, logger, cancellationToken)
                ?? throw new InvalidOperationException($"MPD crime feed returned no statistics for {from:yyyy-MM}.");

            foreach (var feature in doc.GetProperty("features").EnumerateArray())
            {
                var attrs = feature.GetProperty("attributes");
                var rows = attrs.TryGetProperty("n", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0;
                var offenses = attrs.TryGetProperty("s", out var sum) && sum.ValueKind == JsonValueKind.Number ? (int)Math.Round(sum.GetDouble()) : 0;
                var label = string.Join("|", fields.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(f => FieldText(attrs, f)));
                var key = $"{prefix}:{label}";
                var (r0, o0) = fingerprint.GetValueOrDefault(key);
                fingerprint[key] = (r0 + rows, o0 + offenses);
            }
        }

        return fingerprint.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    // The same normalization the importer applies when it stores a value: trimmed, empty when missing.
    private static string FieldText(JsonElement attrs, string field) =>
        !attrs.TryGetProperty(field, out var value) ? "" :
        value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? "" :
        value.ValueKind == JsonValueKind.Number ? value.GetRawText() : "";

    private async Task<Dictionary<string, (int Rows, int Offenses)>> StoredFingerprintAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var range = db.MpdIncidents.AsNoTracking().Where(i => i.OccurredDate >= from && i.OccurredDate <= to);
        var fingerprint = new Dictionary<string, (int, int)>
        {
            ["t:"] = (await range.CountAsync(cancellationToken), await range.SumAsync(i => (int?)i.CrimeCount, cancellationToken) ?? 0)
        };

        foreach (var g in await range.GroupBy(i => new { i.OffenseCategory, i.Offense })
                     .Select(g => new { g.Key.OffenseCategory, g.Key.Offense, Rows = g.Count(), Offenses = g.Sum(x => (int)x.CrimeCount) }).ToListAsync(cancellationToken))
        {
            fingerprint[$"o:{g.OffenseCategory ?? ""}|{g.Offense}"] = (g.Rows, g.Offenses);
        }

        foreach (var g in await range.GroupBy(i => i.Neighborhood)
                     .Select(g => new { g.Key, Rows = g.Count(), Offenses = g.Sum(x => (int)x.CrimeCount) }).ToListAsync(cancellationToken))
        {
            fingerprint[$"n:{g.Key ?? ""}"] = (g.Rows, g.Offenses);
        }

        foreach (var g in await range.GroupBy(i => i.Ward)
                     .Select(g => new { g.Key, Rows = g.Count(), Offenses = g.Sum(x => (int)x.CrimeCount) }).ToListAsync(cancellationToken))
        {
            fingerprint[$"w:{(g.Key is { } w ? w.ToString(System.Globalization.CultureInfo.InvariantCulture) : "")}"] = (g.Rows, g.Offenses);
        }

        return fingerprint.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private static bool SameFingerprint(Dictionary<string, (int Rows, int Offenses)> a, Dictionary<string, (int Rows, int Offenses)> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var other) && other == kv.Value);

    /// <summary>Reads every feed row in the range and makes the stored rows for it identical. Nothing is changed if a page can't be read.</summary>
    private async Task<ReconcileResult> ReconcileRangeAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var where = WhereForRange(from, to);
        var source = new Dictionary<string, MpdIncident>();
        var occurrences = new Dictionary<string, int>();

        for (var page = 0; page < MaxPages; page++)
        {
            var doc = await ArcGisPageFetcher.GetPageAsync(httpClient, RowsUrl(where, page), logger, cancellationToken)
                ?? throw new InvalidOperationException($"MPD crime feed returned no readable page at offset {page * PageSize}.");
            var pageCount = 0;
            foreach (var feature in doc.GetProperty("features").EnumerateArray())
            {
                pageCount++;
                if (feature.TryGetProperty("attributes", out var attrs) && MpdIncidentMapper.FromAttributes(attrs) is { } incident)
                {
                    incident.ExternalKey = UniqueKey(incident.ExternalKey, occurrences);
                    source[incident.ExternalKey] = incident;
                }
            }

            if (pageCount < PageSize)
            {
                break;
            }
        }

        // Rows near the range's edges, since the stored date is Central and the feed's cutoffs are UTC.
        var stored = await db.MpdIncidents
            .Where(i => i.OccurredDate >= from.AddDays(-1) && i.OccurredDate <= to.AddDays(1))
            .ToDictionaryAsync(i => i.ExternalKey, cancellationToken);

        // A row the city revised to a date in this month may be on file under its old date, outside the window above.
        var unseen = source.Keys.Where(k => !stored.ContainsKey(k)).ToList();
        foreach (var chunk in unseen.Chunk(500))
        {
            foreach (var row in await db.MpdIncidents.Where(i => chunk.Contains(i.ExternalKey)).ToListAsync(cancellationToken))
            {
                stored[row.ExternalKey] = row;
            }
        }

        var inserted = 0;
        var updated = 0;
        foreach (var (key, incoming) in source)
        {
            if (stored.TryGetValue(key, out var existing))
            {
                if (CopyChanges(incoming, existing))
                {
                    updated++;
                }
            }
            else
            {
                db.MpdIncidents.Add(incoming);
                inserted++;
            }
        }

        // Rows on file for these dates that the feed no longer lists were withdrawn or moved. A response that would
        // remove a large share at once is more likely a bad read than a real cleanup, so it is left alone.
        var gone = stored.Values.Where(i => i.OccurredDate >= from && i.OccurredDate <= to && !source.ContainsKey(i.ExternalKey)).ToList();
        var inRange = stored.Values.Count(i => i.OccurredDate >= from && i.OccurredDate <= to);
        var deleted = 0;
        var skipped = 0;
        if (gone.Count > Math.Max(MinDeleteAllowance, inRange * MaxDeleteShare))
        {
            skipped = gone.Count;
            logger.LogError("Reconcile of {From} to {To} would remove {Count} of {Total} rows; keeping them", from, to, gone.Count, inRange);
        }
        else
        {
            db.MpdIncidents.RemoveRange(gone);
            deleted = gone.Count;
        }

        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return new ReconcileResult(1, 1, inserted, updated, deleted, skipped);
    }

    /// <summary>Copies the feed's current values onto a stored row; true if anything changed.</summary>
    private static bool CopyChanges(MpdIncident incoming, MpdIncident row)
    {
        var changed = false;
        void Set<T>(Func<MpdIncident, T> get, Action<MpdIncident, T> set)
        {
            if (!EqualityComparer<T>.Default.Equals(get(row), get(incoming)))
            {
                set(row, get(incoming));
                changed = true;
            }
        }

        Set(i => i.OccurredDate, (i, v) => i.OccurredDate = v);
        Set(i => i.OccurredHour, (i, v) => i.OccurredHour = v);
        Set(i => i.GroupKey, (i, v) => i.GroupKey = v);
        Set(i => i.IsCrime, (i, v) => i.IsCrime = v);
        Set(i => i.OffenseCategory, (i, v) => i.OffenseCategory = v);
        Set(i => i.CrimeAgainst, (i, v) => i.CrimeAgainst = v);
        Set(i => i.CrimeCount, (i, v) => i.CrimeCount = v);
        Set(i => i.Neighborhood, (i, v) => i.Neighborhood = v);
        Set(i => i.Ward, (i, v) => i.Ward = v);
        Set(i => i.Precinct, (i, v) => i.Precinct = v);
        Set(i => i.Lat, (i, v) => i.Lat = v);
        Set(i => i.Lng, (i, v) => i.Lng = v);
        Set(i => i.Address, (i, v) => i.Address = v);
        return changed;
    }
}

