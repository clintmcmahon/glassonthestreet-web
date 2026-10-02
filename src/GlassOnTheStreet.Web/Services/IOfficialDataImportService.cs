namespace GlassOnTheStreet.Web.Services;

public record OfficialImportResult(int Fetched, int Imported, int SkippedDuplicate, int SkippedInvalid);

public interface IOfficialDataImportService
{
    /// <summary>
    /// Pulls "Theft From Motor Vehicle" incidents from the City of
    /// Minneapolis open data portal and imports any not already on file
    /// (matched by MPD's case number) as SourceType.OfficialImport reports.
    /// </summary>
    Task<OfficialImportResult> ImportAsync(int lookbackDays, CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports the same categories for a Central Time date range (inclusive).
    /// Idempotent, so a range can be rerun safely after an interruption.
    /// </summary>
    Task<OfficialImportResult> ImportRangeAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    /// <summary>
    /// True while an import (manual or the daily background sync) is
    /// actually running. A large lookback can take minutes -- long past
    /// nginx's default proxy read timeout -- so the admin UI kicks the
    /// import off in the background and polls this instead of blocking the
    /// request on it.
    /// </summary>
    bool IsRunning { get; }
}
