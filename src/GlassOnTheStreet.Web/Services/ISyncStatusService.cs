namespace GlassOnTheStreet.Web.Services;

public interface ISyncStatusService
{
    Task<DateTime?> GetLastSyncedAtAsync(CancellationToken cancellationToken = default);

    Task RecordSyncAsync(CancellationToken cancellationToken = default);
}
