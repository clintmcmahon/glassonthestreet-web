using GlassOnTheStreet.Web.Data;
using GlassOnTheStreet.Web.Infrastructure;
using GlassOnTheStreet.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace GlassOnTheStreet.Web.Services;

/// <param name="CoveredBy">Earlier one-time tasks whose completion already covers this one.</param>
public record ExpectedTask(string Key, string Description, IReadOnlyList<string> CoveredBy);

/// <summary>
/// Keeps the MPD-sourced data current without anyone clicking Import. Runs
/// shortly after startup and then once a day.
///
/// History is loaded one calendar year at a time, each year its own
/// marker row (OneTimeTasks). That matters because every deploy restarts the
/// app: an import cancelled by a restart loses at most the year it was in,
/// and the next start skips every year already marked done. Imports are
/// additive and deduplicated (nothing is deleted), so a rerun is always safe.
///
///   CarImport-v2-{year}    the four car-related categories shown on the map (Reports). v2: rerun after
///                          the service-area box was widened past 45.05 (far north Minneapolis was being rejected)
///   IncidentImport-{year}  the full MPD feed, every offense (MpdIncidents)
///   IncidentReconcile-*    once, every month since 2019 compared with the city's own statistics and made to match.
///                          Needed because MPD posts records weeks or months late and withdraws or revises others,
///                          which a one-time additive import never sees. The daily sync repeats this for the last
///                          24 months.
///
/// The history pass runs on every tick, not only at startup, so a year that
/// failed (the city feed was down) is retried the next day.
///
/// The old one-shot tasks ("CleanReimport...", "BackfillFrom2019...") are not trusted
/// here: they marked themselves done after runs that a failed page had silently
/// truncated, so every year is verified by importing it (deduplicated, so rows already
/// on file are skipped).
///
/// A third one-time task re-anchors resident reports saved under the old grid
/// snapping to MPD's block midpoints; it runs once the car data is loaded.
///
/// The importers and GlassOnTheStreetContext are scoped, but a
/// BackgroundService is a singleton, so each run gets its own IServiceScope.
/// </summary>
public class OfficialDataSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<OfficialDataSyncBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SyncInterval = TimeSpan.FromDays(1);

    // MPD posts some records late, so each daily sync looks back a month.
    private const int LookbackDays = 30;

    // MPD's current records feed starts here; earlier dates are stray rows.
    public const int FirstYear = 2019;

    public const string ResnapUserReportsTaskKey = "ResnapUserReports2026-09-30";

    public const string ClearUnplacedLocationsTaskKey = "ClearUnplacedLocations2026-10-05";

    public const string ReconcileHistoryTaskKey = "IncidentReconcile-2026-10-05";

    // How far back the daily sync re-checks month totals against the feed.
    private const int ReconcileMonths = 24;

    public static string CarImportKey(int year) => $"CarImport-v2-{year}";

    public static string IncidentImportKey(int year) => $"IncidentImport-{year}";

    /// <summary>The startup tasks, in the order they run, for the admin Data status panel.</summary>
    public static IReadOnlyList<ExpectedTask> ExpectedTasks(int currentYear)
    {
        var tasks = new List<ExpectedTask>();
        for (var year = FirstYear; year <= currentYear; year++)
        {
            tasks.Add(new ExpectedTask(CarImportKey(year), $"Car-related MPD categories, {year}", []));
        }

        tasks.Add(new ExpectedTask(ResnapUserReportsTaskKey, "Re-anchor resident reports to block midpoints", []));
        tasks.Add(new ExpectedTask(ClearUnplacedLocationsTaskKey, "Clear 0,0 locations MPD sent for records it could not place", []));
        tasks.Add(new ExpectedTask(ReconcileHistoryTaskKey, "Match every month since 2019 to the city's feed (late-posted, revised and withdrawn rows)", []));

        for (var year = FirstYear; year <= currentYear; year++)
        {
            tasks.Add(new ExpectedTask(IncidentImportKey(year), $"All MPD offenses, {year}", []));
        }

        return tasks;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunHistoryAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Network blip or city portal downtime: nothing was marked
                // done for the failed year, so the next tick picks it up.
                logger.LogWarning(ex, "MPD history import failed; will retry on the next interval");
            }

            try
            {
                await RunDailySyncAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Scheduled MPD sync failed; will retry on the next interval");
            }

            try
            {
                await Task.Delay(SyncInterval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunDailySyncAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var today = CentralToday();

        var cars = scope.ServiceProvider.GetRequiredService<IOfficialDataImportService>();
        var carResult = await cars.ImportAsync(LookbackDays, cancellationToken);
        logger.LogInformation(
            "Scheduled MPD sync (car categories): fetched {Fetched}, imported {Imported} new, skipped {SkippedDuplicate} duplicate / {SkippedInvalid} invalid",
            carResult.Fetched, carResult.Imported, carResult.SkippedDuplicate, carResult.SkippedInvalid);

        var incidents = scope.ServiceProvider.GetRequiredService<IMpdIncidentImportService>();
        var incidentResult = await incidents.ImportRangeAsync(today.AddDays(-LookbackDays), today, cancellationToken);
        logger.LogInformation(
            "Scheduled MPD sync (all offenses): fetched {Fetched}, imported {Imported} new, skipped {SkippedDuplicate} duplicate / {SkippedInvalid} invalid",
            incidentResult.Fetched, incidentResult.Imported, incidentResult.SkippedDuplicate, incidentResult.SkippedInvalid);

        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var reconcile = await incidents.ReconcileMonthsAsync(thisMonth.AddMonths(-(ReconcileMonths - 1)), thisMonth, cancellationToken);
        logger.LogInformation(
            "Scheduled MPD reconcile: {Checked} months checked, {Changed} differed (added {Inserted}, updated {Updated}, removed {Deleted}, kept {Skipped})",
            reconcile.MonthsChecked, reconcile.MonthsChanged, reconcile.Inserted, reconcile.Updated, reconcile.Deleted, reconcile.DeletesSkipped);

        var syncStatusService = scope.ServiceProvider.GetRequiredService<ISyncStatusService>();
        await syncStatusService.RecordSyncAsync(cancellationToken);
    }

    private async Task RunHistoryAsync(CancellationToken cancellationToken)
    {
        var today = CentralToday();

        for (var year = FirstYear; year <= today.Year; year++)
        {
            await RunChunkAsync(CarImportKey(year), [], year, today, async (scope, from, to) =>
                await scope.ServiceProvider.GetRequiredService<IOfficialDataImportService>().ImportRangeAsync(from, to, cancellationToken),
                cancellationToken);
        }

        try
        {
            await RunResnapUserReportsOnceAsync(today.Year, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not worth blocking the incident import over; retried next tick.
            logger.LogError(ex, "Resident report re-anchoring failed; will retry on the next interval");
        }

        for (var year = FirstYear; year <= today.Year; year++)
        {
            await RunChunkAsync(IncidentImportKey(year), [], year, today, async (scope, from, to) =>
                await scope.ServiceProvider.GetRequiredService<IMpdIncidentImportService>().ImportRangeAsync(from, to, cancellationToken),
                cancellationToken);
        }

        using var cleanupScope = scopeFactory.CreateScope();
        await ClearUnplacedLocationsOnceAsync(cleanupScope.ServiceProvider.GetRequiredService<GlassOnTheStreetContext>(), cancellationToken);

        using var reconcileScope = scopeFactory.CreateScope();
        await ReconcileHistoryOnceAsync(reconcileScope.ServiceProvider, today, cancellationToken);
    }

    /// <summary>Once: every month since the first year, compared with the feed and made to match. Marked done only when the whole pass finished.</summary>
    public async Task ReconcileHistoryOnceAsync(IServiceProvider services, DateOnly today, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<GlassOnTheStreetContext>();
        if (await db.OneTimeTasks.AnyAsync(t => t.Key == ReconcileHistoryTaskKey, cancellationToken))
        {
            return;
        }

        var result = await services.GetRequiredService<IMpdIncidentImportService>()
            .ReconcileMonthsAsync(new DateOnly(FirstYear, 1, 1), new DateOnly(today.Year, today.Month, 1), cancellationToken);
        logger.LogWarning(
            "History reconcile: {Checked} months checked, {Changed} differed (added {Inserted}, updated {Updated}, removed {Deleted}, kept {Skipped})",
            result.MonthsChecked, result.MonthsChanged, result.Inserted, result.Updated, result.Deleted, result.DeletesSkipped);

        // A skipped delete means a month still disagrees with the feed: leave the task open so it is looked at again.
        if (result.DeletesSkipped == 0)
        {
            db.OneTimeTasks.Add(new OneTimeTask { Key = ReconcileHistoryTaskKey });
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Earlier imports stored the 0,0 MPD sends for a record it couldn't place as a real point (and "No Address" as an
    /// address). Turns those into "no location", once. Returns how many rows changed.
    /// </summary>
    public async Task<int> ClearUnplacedLocationsOnceAsync(GlassOnTheStreetContext db, CancellationToken cancellationToken)
    {
        if (await db.OneTimeTasks.AnyAsync(t => t.Key == ClearUnplacedLocationsTaskKey, cancellationToken))
        {
            return 0;
        }

        var rows = await db.MpdIncidents.Where(i => i.Lat == 0 && i.Lng == 0).ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            row.Lat = null;
            row.Lng = null;
            if (string.Equals(row.Address, "No Address", StringComparison.OrdinalIgnoreCase))
            {
                row.Address = null;
            }
        }

        db.OneTimeTasks.Add(new OneTimeTask { Key = ClearUnplacedLocationsTaskKey });
        await db.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Cleared the 0,0 location on {Count} MPD rows MPD could not place", rows.Count);
        return rows.Count;
    }

    private async Task RunChunkAsync(
        string key, IReadOnlyList<string> coveredBy, int year, DateOnly today,
        Func<IServiceScope, DateOnly, DateOnly, Task<OfficialImportResult>> import,
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GlassOnTheStreetContext>();

        if (await IsDoneAsync(db, key, coveredBy, cancellationToken))
        {
            return;
        }

        var from = new DateOnly(year, 1, 1);
        var to = year == today.Year ? today : new DateOnly(year, 12, 31);
        logger.LogWarning("Importing {Key}: {From} to {To}", key, from, to);

        var result = await import(scope, from, to);
        logger.LogInformation(
            "{Key} finished: fetched {Fetched}, imported {Imported} new, skipped {SkippedDuplicate} duplicate / {SkippedInvalid} invalid",
            key, result.Fetched, result.Imported, result.SkippedDuplicate, result.SkippedInvalid);

        // Marked only after the whole range was read. A restart before this
        // line repeats the year, which is safe because rows are deduplicated.
        db.OneTimeTasks.Add(new OneTimeTask { Key = key });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<bool> IsDoneAsync(
        GlassOnTheStreetContext db, string key, IReadOnlyList<string> coveredBy, CancellationToken cancellationToken)
    {
        var keys = coveredBy.Append(key).ToList();
        return await db.OneTimeTasks.AnyAsync(t => keys.Contains(t.Key), cancellationToken);
    }

    private async Task RunResnapUserReportsOnceAsync(int currentYear, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GlassOnTheStreetContext>();

        if (await db.OneTimeTasks.AnyAsync(t => t.Key == ResnapUserReportsTaskKey, cancellationToken))
        {
            return;
        }

        // The car categories must be loaded first: this job looks up MPD's
        // own block anchors. If a year is still missing, wait for the next tick.
        for (var year = FirstYear; year <= currentYear; year++)
        {
            if (!await IsDoneAsync(db, CarImportKey(year), [], cancellationToken))
            {
                return;
            }
        }

        var anchors = scope.ServiceProvider.GetRequiredService<IBlockAnchorService>();
        var reports = await db.Reports
            .Where(r => r.SourceType == SourceType.UserReport)
            .ToListAsync(cancellationToken);

        logger.LogWarning("Re-anchoring {Count} resident reports to block midpoints", reports.Count);

        var moved = 0;
        var skipped = 0;
        foreach (var report in reports)
        {
            // Only the previously snapped point is available here (the
            // precise one was never stored), so this is best effort:
            // a report near a block boundary can land on a neighboring block.
            var anchor = await anchors.SnapAsync(report.DisplayLat, report.DisplayLng, cancellationToken);
            if (anchor.Method == "coarse-grid")
            {
                // Leave it: replacing a block-level point with a coarser one would lose information.
                logger.LogWarning("Report {ReportId}: no block anchor found, left unchanged", report.Id);
                skipped++;
            }
            else
            {
                logger.LogInformation(
                    "Report {ReportId}: ({OldLat}, {OldLng}) -> ({NewLat}, {NewLng}) via {Method}",
                    report.Id, report.DisplayLat, report.DisplayLng, anchor.Lat, anchor.Lng, anchor.Method);
                report.DisplayLat = anchor.Lat;
                report.DisplayLng = anchor.Lng;
                moved++;
            }

            // Nominatim's usage policy: at most one request per second.
            await Task.Delay(TimeSpan.FromMilliseconds(1100), cancellationToken);
        }

        db.OneTimeTasks.Add(new OneTimeTask { Key = ResnapUserReportsTaskKey });
        await db.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Re-anchored resident reports: {Moved} moved, {Skipped} left unchanged", moved, skipped);
    }

    private static DateOnly CentralToday() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, CentralTime.Zone));
}
