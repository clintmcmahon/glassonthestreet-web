namespace GlassOnTheStreet.Web.Services;

/// <summary>
/// Keeps the MPD-sourced side of the map current without anyone having to
/// remember to click Import in /admin. Runs once shortly after startup and
/// then once a day. A short lookback (3 days) is enough to overlap the
/// city portal's own daily refresh cadence -- ExternalCaseNumber dedup
/// (see MinneapolisOpenDataImportService) makes re-fetching already-seen
/// records a no-op, so overlap is harmless.
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
                using var scope = scopeFactory.CreateScope();
                var importService = scope.ServiceProvider.GetRequiredService<IOfficialDataImportService>();
                var result = await importService.ImportAsync(LookbackDays, stoppingToken);

                logger.LogInformation(
                    "Scheduled MPD sync: fetched {Fetched}, imported {Imported} new, skipped {SkippedDuplicate} duplicate / {SkippedInvalid} invalid",
                    result.Fetched, result.Imported, result.SkippedDuplicate, result.SkippedInvalid);
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
}
