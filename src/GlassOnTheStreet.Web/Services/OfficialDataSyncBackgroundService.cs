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

    private const string CleanReimportTaskKey = "CleanReimport2026-09-28";
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
}
