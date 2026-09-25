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
}
