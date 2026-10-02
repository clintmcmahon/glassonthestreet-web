namespace GlassOnTheStreet.Web.Services;

public interface IMpdIncidentImportService
{
    /// <summary>
    /// Imports every offense MPD published for the Central Time date range
    /// (inclusive) that isn't already on file. Idempotent: rows are matched by
    /// case number, NIBRS code and offense, and each page is saved as it is
    /// read, so an interrupted run keeps its progress and a rerun skips it.
    /// </summary>
    Task<OfficialImportResult> ImportRangeAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    /// <summary>True while an import is running (manual, daily or startup).</summary>
    bool IsRunning { get; }
}
