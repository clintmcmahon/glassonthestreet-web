using GlassOnTheStreet.Web.Data;
using GlassOnTheStreet.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace GlassOnTheStreet.Web.Services;

public class SyncStatusService(GlassOnTheStreetContext db) : ISyncStatusService
{
    private const int SingletonId = 1;

    private const int MaxErrorLength = 500;

    private const int MaxStepLength = 200;

    public async Task<DateTime?> GetLastSyncedAtAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken);
        // A row created by a failure or a step before any success has no successful sync yet.
        return status is { LastSyncedAt: var at } && at != default ? at : null;
    }

    public Task<SyncStatus?> GetStatusAsync(CancellationToken cancellationToken = default) =>
        db.SyncStatuses.FirstOrDefaultAsync(s => s.Id == SingletonId, cancellationToken);

    public async Task RecordSyncAsync(bool clearFailure = true, CancellationToken cancellationToken = default)
    {
        var status = await LoadOrCreateAsync(cancellationToken);
        status.LastSyncedAt = DateTime.UtcNow;
        if (clearFailure)
        {
            status.LastFailedAt = null;
            status.LastError = null;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<SyncFailure?> GetLastFailureAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken);
        return status is { LastFailedAt: { } at, LastError: { } error } ? new SyncFailure(at, error) : null;
    }

    public async Task RecordFailureAsync(string error, CancellationToken cancellationToken = default)
    {
        // No success on record yet leaves LastSyncedAt at default, which GetLastSyncedAtAsync reads as "never".
        var status = await LoadOrCreateAsync(cancellationToken);
        status.LastFailedAt = DateTime.UtcNow;
        status.LastError = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordStepAsync(string? step, CancellationToken cancellationToken = default)
    {
        var status = await LoadOrCreateAsync(cancellationToken);
        status.CurrentStep = step is { Length: > MaxStepLength } ? step[..MaxStepLength] : step;
        status.CurrentStepStartedAt = step is null ? null : DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordFeedEditedAsync(DateTime editedAtUtc, CancellationToken cancellationToken = default)
    {
        var status = await LoadOrCreateAsync(cancellationToken);
        status.FeedLastEditedAt = editedAtUtc;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<SyncStatus> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken);
        if (status is null)
        {
            status = new SyncStatus { Id = SingletonId };
            db.SyncStatuses.Add(status);
        }

        return status;
    }
}
