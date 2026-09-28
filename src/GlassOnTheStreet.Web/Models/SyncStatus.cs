namespace GlassOnTheStreet.Web.Models;

// A single row (Id = SingletonId in SyncStatusService) tracking when MPD
// data was last successfully synced -- separate from Report data entirely,
// since "the sync ran and found nothing new" is a real, valid outcome that
// MAX(Report.CreatedAt) can't distinguish from "the sync hasn't run in
// days."
public class SyncStatus
{
    public int Id { get; set; }

    public DateTime LastSyncedAt { get; set; }
}
