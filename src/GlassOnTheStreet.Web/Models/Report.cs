using System.ComponentModel.DataAnnotations;

namespace GlassOnTheStreet.Web.Models;

public enum IncidentType
{
    WindowSmashed,
    Rifled,

    // MPD's open data doesn't record entry method for a theft-from-vehicle
    // report, so imported records can't be classified as either of the
    // above -- this is that honest third option, never used for reports
    // submitted through the site itself.
    Unknown
}

public enum TimeOfDay
{
    Overnight,
    Morning,
    Afternoon,
    Evening,
    NotSure
}

public enum ReportStatus
{
    Active,
    Flagged,
    Removed
}

public enum SourceType
{
    UserReport,
    OfficialImport
}

public class Report
{
    public int Id { get; set; }

    [Required]
    public DateOnly ReportedDate { get; set; }

    // The precise point someone taps or searches for is snapped to the
    // block before it ever reaches this model. It is never stored, not
    // even privately -- there is no PreciseLat/PreciseLng column.
    [Required]
    public decimal DisplayLat { get; set; }

    [Required]
    public decimal DisplayLng { get; set; }

    [Required]
    public IncidentType IncidentType { get; set; }

    public TimeOfDay? TimeOfDay { get; set; }

    public bool? ItemsStolen { get; set; }

    public bool? PoliceReported { get; set; }

    [MaxLength(200)]
    public string? CrossStreets { get; set; }

    [MaxLength(260)]
    public string? PhotoPath { get; set; }

    // Best-effort, filled in at submission time via reverse geocoding for
    // user reports, or copied directly from MPD's data for imports. Used
    // for the neighborhood breakdown stat; absent if lookup fails.
    [MaxLength(120)]
    public string? Neighborhood { get; set; }

    public ReportStatus Status { get; set; } = ReportStatus.Active;

    public SourceType SourceType { get; set; } = SourceType.UserReport;

    // MPD's Case_Number, present only on SourceType.OfficialImport rows.
    // Used to make re-running the importer idempotent.
    [MaxLength(60)]
    public string? ExternalCaseNumber { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<ReportFlag> Flags { get; set; } = new();
}
