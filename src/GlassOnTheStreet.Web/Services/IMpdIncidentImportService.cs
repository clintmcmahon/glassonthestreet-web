namespace GlassOnTheStreet.Web.Services;

/// <param name="MonthsChecked">Months whose counts were compared with the city's own statistics.</param>
/// <param name="MonthsChanged">Months that differed and were brought into line with the feed.</param>
/// <param name="DeletesSkipped">Rows that looked removed from the feed but were kept, because too many at once suggests a bad response.</param>
public record ReconcileResult(int MonthsChecked, int MonthsChanged, int Inserted, int Updated, int Deleted, int DeletesSkipped);

public interface IMpdIncidentImportService
{
    /// <summary>
    /// Imports every offense MPD published for the Central Time date range
    /// (inclusive) that isn't already on file. Idempotent: rows are matched by
    /// case number, NIBRS code and offense, and each page is saved as it is
    /// read, so an interrupted run keeps its progress and a rerun skips it.
    /// </summary>
    Task<OfficialImportResult> ImportRangeAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes every month from <paramref name="firstMonth"/> through <paramref name="lastMonth"/> match the city's feed. Each
    /// month's row count and offense total are compared with the feed's own statistics (one cheap query); a month that
    /// differs is read in full and synced: late-posted rows are added, revised rows updated, rows the city no longer
    /// publishes removed. The additive import can't do that: MPD posts records weeks or months late and revises or
    /// withdraws others, so without this the database drifts from the feed.
    /// </summary>
    Task<ReconcileResult> ReconcileMonthsAsync(DateOnly firstMonth, DateOnly lastMonth, CancellationToken cancellationToken = default);

    /// <summary>True while an import is running (manual, daily or startup).</summary>
    bool IsRunning { get; }
}
