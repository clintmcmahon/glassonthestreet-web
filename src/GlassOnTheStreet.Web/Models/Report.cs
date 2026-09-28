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
    Unknown,

    // The whole car got stolen, not broken into -- a different, but
    // definitively *known*, crime. Imported from MPD's separate "Motor
    // Vehicle Theft" category. Deliberately not folded into Unknown:
    // Unknown means "we know it was a break-in, we don't know how," while
    // this means "there was no break-in to classify at all."
    VehicleStolen,

    // MPD's "Theft of Motor Vehicle Parts or Accessories" category --
    // catalytic converters, wheels, stereos taken off/out of a car, which
    // doesn't necessarily involve breaking a window or even entering the
    // car at all. Kept distinct from Unknown (a real break-in, entry method
    // unrecorded) rather than conflated with it, for the same reason
    // VehicleStolen is separate: it's a meaningfully different crime, not
    // an unclassified version of the same one. Appended at the end, not
    // inserted, so existing stored int values don't shift.
    PartsTheft
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
    Removed,

    // Resident submissions land here and need an admin to approve them
    // before they're publicly visible (GetReports/GetStats/GetBreakdown
    // all filter to Active only, so Pending rows are simply invisible
    // until approved). Appended at the end, not inserted, so existing
    // stored int values for Active/Flagged/Removed don't shift.
    Pending
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

    // Only populated for SourceType.OfficialImport rows, straight from
    // MPD's own Ward/Precinct fields -- residents won't reliably know
    // either, so we don't ask. Ward is what a city council member
    // represents, so it's the most direct "here's the evidence" unit for
    // the site's whole pitch to city hall.
    public int? Ward { get; set; }

    public int? Precinct { get; set; }

    public ReportStatus Status { get; set; } = ReportStatus.Active;

    public SourceType SourceType { get; set; } = SourceType.UserReport;

    // MPD's Case_Number, present only on SourceType.OfficialImport rows.
    // Used to make re-running the importer idempotent.
    [MaxLength(60)]
    public string? ExternalCaseNumber { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<ReportFlag> Flags { get; set; } = new();
    public string? Offense {get;set;} = string.Empty;
    public string? Address {get;set;} = string.Empty;

    // Everything below is only populated for SourceType.OfficialImport rows,
    // straight from the remaining fields on MPD's open data feed (see
    // MinneapolisOpenDataImportService). Deliberately NOT included: the
    // feed's own Latitude/Longitude fields -- verified precise (identical to
    // the geometry point, not anonymized despite wgsXAnon/wgsYAnon's
    // naming) -- since this site never stores a precise location for any
    // report, including MPD's own.

    // The feed's own record-type label (e.g. "Crime Offenses (NIBRS)").
    [MaxLength(24)]
    public string? MpdType { get; set; }

    // "DID" in the feed -- sparsely populated, meaning unconfirmed, but
    // captured for completeness.
    [MaxLength(20)]
    public string? MpdIncidentId { get; set; }

    [MaxLength(60)]
    public string? AlternateCaseNumber { get; set; }

    // When MPD logged the report -- distinct from ReportedDate (which for
    // an import holds Occurred_Date, when the incident actually happened).
    public DateOnly? MpdReportedDate { get; set; }

    [MaxLength(50)]
    public string? NibrsCrimeAgainst { get; set; }

    [MaxLength(50)]
    public string? NibrsGroup { get; set; }

    [MaxLength(50)]
    public string? NibrsCode { get; set; }

    [MaxLength(50)]
    public string? OffenseCategory { get; set; }

    // CAD dispatch call-type fields -- generic and cross-contaminated across
    // offense categories (see MinneapolisOpenDataImportService's doc comment
    // on why they can't isolate vehicle-specific vandalism), but captured
    // as raw reference data since they're part of the feed.
    [MaxLength(400)]
    public string? ProblemInitial { get; set; }

    [MaxLength(30)]
    public string? ProblemFinal { get; set; }

    public int? CrimeCount { get; set; }
}
