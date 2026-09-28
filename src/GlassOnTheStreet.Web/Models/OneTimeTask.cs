namespace GlassOnTheStreet.Web.Models;

// A durable marker so a one-time startup task (e.g. a full data reimport)
// runs exactly once across every deploy/restart, not on every app startup.
// A DB row rather than a file: the deploy pipeline publishes a fresh
// filesystem on every deploy, so a marker file would never survive.
public class OneTimeTask
{
    public int Id { get; set; }

    public required string Key { get; set; }

    public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
}
