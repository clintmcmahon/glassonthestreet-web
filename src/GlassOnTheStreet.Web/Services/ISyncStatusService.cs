namespace GlassOnTheStreet.Web.Services;

public record SyncFailure(DateTime At, string Error);

public interface ISyncStatusService
{
    Task<DateTime?> GetLastSyncedAtAsync(CancellationToken cancellationToken = default);

    Task RecordSyncAsync(CancellationToken cancellationToken = default);

    Task<SyncFailure?> GetLastFailureAsync(CancellationToken cancellationToken = default);

    Task RecordFailureAsync(string error, CancellationToken cancellationToken = default);
}
