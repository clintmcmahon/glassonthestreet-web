using System.ComponentModel.DataAnnotations;

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

    // The most recent failed daily sync, cleared by the next success. Lets /admin
    // show why the timestamp above has stopped moving.
    public DateTime? LastFailedAt { get; set; }

    [MaxLength(500)]
    public string? LastError { get; set; }

    // The step the background sync is on, null between runs. A step whose start time keeps getting older is a hang:
    // nothing throws, so LastError alone would never show it.
    [MaxLength(200)]
    public string? CurrentStep { get; set; }

    public DateTime? CurrentStepStartedAt { get; set; }

    // When the city last republished its crime feed (the layer's dataLastEditDate). When this stops moving, the
    // sync still succeeds but has nothing new to import, which is a problem on the city's side, not ours.
    public DateTime? FeedLastEditedAt { get; set; }
}
