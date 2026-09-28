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
        return status?.LastSyncedAt;
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
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
