using GlassOnTheStreet.Web.Models;

namespace GlassOnTheStreet.Web.Services;

public record SyncFailure(DateTime At, string Error);

public interface ISyncStatusService
{
    Task<DateTime?> GetLastSyncedAtAsync(CancellationToken cancellationToken = default);

    /// <summary>The whole status row for /admin, or null before the first sync touched it.</summary>
    Task<SyncStatus?> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Marks a successful sync. <paramref name="clearFailure"/> false keeps an error another step of the same run recorded.</summary>
    Task RecordSyncAsync(bool clearFailure = true, CancellationToken cancellationToken = default);

    Task<SyncFailure?> GetLastFailureAsync(CancellationToken cancellationToken = default);

    Task RecordFailureAsync(string error, CancellationToken cancellationToken = default);

    /// <summary>The step the sync is starting, or null when the run is over.</summary>
    Task RecordStepAsync(string? step, CancellationToken cancellationToken = default);

    Task RecordFeedEditedAsync(DateTime editedAtUtc, CancellationToken cancellationToken = default);
}
