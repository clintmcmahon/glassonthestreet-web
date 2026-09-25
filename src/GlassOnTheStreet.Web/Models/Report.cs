using System.ComponentModel.DataAnnotations;

namespace GlassOnTheStreet.Web.Models;

public enum IncidentType
{
    WindowSmashed,
    Rifled
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

    public ReportStatus Status { get; set; } = ReportStatus.Active;

    public SourceType SourceType { get; set; } = SourceType.UserReport;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<ReportFlag> Flags { get; set; } = new();
}
