using GlassOnTheStreet.Web.Data;
using GlassOnTheStreet.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace GlassOnTheStreet.Web.Services;

public class SyncStatusService(GlassOnTheStreetContext db) : ISyncStatusService
{
    private const int SingletonId = 1;

    public async Task<DateTime?> GetLastSyncedAtAsync(CancellationToken cancellationToken = default)
    {
        var status = await db.SyncStatuses.FirstOrDefaultAsync(s => s.Id == SingletonId, cancellationToken);
        // A row created by a first-ever failure has no successful sync yet.
        return status is { LastSyncedAt: var at } && at != default ? at : null;
    }

    public async Task RecordSyncAsync(CancellationToken cancellationToken = default)
    {
        var status = await db.SyncStatuses.FirstOrDefaultAsync(s => s.Id == SingletonId, cancellationToken);
        if (status is null)
        {
            db.SyncStatuses.Add(new SyncStatus { Id = SingletonId, LastSyncedAt = DateTime.UtcNow });
        }
        else
        {
            status.LastSyncedAt = DateTime.UtcNow;
            status.LastFailedAt = null;
            status.LastError = null;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<SyncFailure?> GetLastFailureAsync(CancellationToken cancellationToken = default)
    {
        var status = await db.SyncStatuses.FirstOrDefaultAsync(s => s.Id == SingletonId, cancellationToken);
        return status is { LastFailedAt: { } at, LastError: { } error } ? new SyncFailure(at, error) : null;
    }

    public async Task RecordFailureAsync(string error, CancellationToken cancellationToken = default)
    {
        var status = await db.SyncStatuses.FirstOrDefaultAsync(s => s.Id == SingletonId, cancellationToken);
        if (status is null)
        {
            // No success on record yet: LastSyncedAt = default would read as year 1, so keep the row but flag it via the failure fields.
            status = new SyncStatus { Id = SingletonId };
            db.SyncStatuses.Add(status);
        }

        status.LastFailedAt = DateTime.UtcNow;
        status.LastError = error.Length > 500 ? error[..500] : error;
        await db.SaveChangesAsync(cancellationToken);
    }
}
