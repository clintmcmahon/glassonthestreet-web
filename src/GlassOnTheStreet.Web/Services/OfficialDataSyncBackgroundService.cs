using GlassOnTheStreet.Web.Data;
using GlassOnTheStreet.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace GlassOnTheStreet.Web.Services;

/// <summary>
/// Keeps the MPD-sourced side of the map current without anyone having to
/// remember to click Import in /admin. Runs once shortly after startup and
/// then once a day. A short lookback (3 days) is enough to overlap the
/// city portal's own daily refresh cadence -- ExternalCaseNumber dedup
/// (see MinneapolisOpenDataImportService) makes re-fetching already-seen
/// records a no-op, so overlap is harmless.
///
/// Before falling into that regular loop, it runs a one-time clean
/// reimport exactly once (guarded by a OneTimeTasks marker row, since a
/// fresh deploy publishes a fresh filesystem -- a marker file wouldn't
/// survive): every existing MPD-imported report is deleted and the full
/// history from CleanReimportStartDate is re-fetched from scratch. This
/// exists to recover from data accumulated across this service's various
/// bug fixes and category changes (dead categories, miscounted vandalism,
/// etc.) with a genuinely clean slate, rather than trying to patch old
/// rows in place. Resident-submitted reports (SourceType.UserReport) are
/// never touched by this.
///
/// The clean reimport is rerun when the importer changes what it stores
/// (2026-09-30: MPD's own block anchors are now kept as-is instead of being
/// re-snapped to a grid). Once it has run, a second one-time task re-anchors
/// resident reports that were saved under the old grid snapping, using the
/// MPD anchors the reimport just loaded.
///
/// IOfficialDataImportService and GlassOnTheStreetContext are scoped, but
/// a BackgroundService is a singleton, so each run gets its own
/// IServiceScope rather than holding one for the app's whole lifetime.
/// </summary>
public class OfficialDataSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<OfficialDataSyncBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SyncInterval = TimeSpan.FromDays(1);
    private const int LookbackDays = 3;

    private const string CleanReimportTaskKey = "CleanReimport2026-09-30";
    private const string ResnapUserReportsTaskKey = "ResnapUserReports2026-09-30";
    private static readonly DateOnly CleanReimportStartDate = new(2020, 1, 1);

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

        await RunCleanReimportOnceAsync(stoppingToken);
        await RunResnapUserReportsOnceAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var importService = scope.ServiceProvider.GetRequiredService<IOfficialDataImportService>();
                var result = await importService.ImportAsync(LookbackDays, stoppingToken);

                logger.LogInformation(
                    "Scheduled MPD sync: fetched {Fetched}, imported {Imported} new, skipped {SkippedDuplicate} duplicate / {SkippedInvalid} invalid",
                    result.Fetched, result.Imported, result.SkippedDuplicate, result.SkippedInvalid);

                var syncStatusService = scope.ServiceProvider.GetRequiredService<ISyncStatusService>();
                await syncStatusService.RecordSyncAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed sync (network blip, city portal downtime) should
                // never take the app down -- just try again on the next tick.
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

    private async Task RunCleanReimportOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GlassOnTheStreetContext>();

            if (await db.OneTimeTasks.AnyAsync(t => t.Key == CleanReimportTaskKey, cancellationToken))
            {
                return;
            }

            logger.LogWarning(
                "Running one-time clean reimport: deleting all existing MPD-imported reports and re-importing from {StartDate}",
                CleanReimportStartDate);

            var deleted = await db.Reports
                .Where(r => r.SourceType == SourceType.OfficialImport)
                .ExecuteDeleteAsync(cancellationToken);
            logger.LogInformation("Clean reimport: deleted {Deleted} existing MPD-imported reports", deleted);

            var lookbackDays = (int)(DateOnly.FromDateTime(DateTime.UtcNow).ToDateTime(TimeOnly.MinValue)
                - CleanReimportStartDate.ToDateTime(TimeOnly.MinValue)).TotalDays;
            var importService = scope.ServiceProvider.GetRequiredService<IOfficialDataImportService>();
            var result = await importService.ImportAsync(lookbackDays, cancellationToken);
            logger.LogInformation(
                "Clean reimport finished: fetched {Fetched}, imported {Imported} new, skipped {SkippedDuplicate} duplicate / {SkippedInvalid} invalid",
                result.Fetched, result.Imported, result.SkippedDuplicate, result.SkippedInvalid);

            var syncStatusService = scope.ServiceProvider.GetRequiredService<ISyncStatusService>();
            await syncStatusService.RecordSyncAsync(cancellationToken);

            db.OneTimeTasks.Add(new OneTimeTask { Key = CleanReimportTaskKey });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately does NOT mark the task complete on failure --
            // an interrupted or failed clean reimport should retry on the
            // next startup rather than silently leave the data half-wiped.
            logger.LogError(ex, "One-time clean reimport failed; will retry on next startup");
        }
    }

    private async Task RunResnapUserReportsOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GlassOnTheStreetContext>();

            if (await db.OneTimeTasks.AnyAsync(t => t.Key == ResnapUserReportsTaskKey, cancellationToken))
            {
                return;
            }

            // The clean reimport marker is what proves the MPD anchors this
            // job looks up are the unsnapped ones. If it didn't complete,
            // wait for the next startup rather than anchor to stale points.
            if (!await db.OneTimeTasks.AnyAsync(t => t.Key == CleanReimportTaskKey, cancellationToken))
            {
                return;
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Resident report re-anchoring failed; will retry on next startup");
        }
    }
}
