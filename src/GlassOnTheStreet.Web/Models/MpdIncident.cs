using System.ComponentModel.DataAnnotations;

namespace GlassOnTheStreet.Web.Models;

/// <summary>
/// One row of Minneapolis Police Department's open crime feed, any offense.
/// Separate from <see cref="Report"/> (resident reports plus the four
/// car-related MPD categories shown on the map) so the all-offense statistics
/// can be 3x the size without touching the map. Coordinates are MPD's own
/// anonymized block midpoints, stored as given.
/// </summary>
public class MpdIncident
{
    public int Id { get; set; }

    /// <summary>Case number, NIBRS code and offense: unique per row in the feed.</summary>
    [Required, MaxLength(160)]
    public required string ExternalKey { get; set; }

    [Required, MaxLength(20)]
    public required string CaseNumber { get; set; }

    /// <summary>Date the offense occurred, in Central Time.</summary>
    public DateOnly OccurredDate { get; set; }

    /// <summary>Hour of day (0 to 23) the offense occurred, Central Time. Often rounded to the hour by whoever reported it.</summary>
    public byte OccurredHour { get; set; }

    /// <summary>Our plain-language group; see CrimeGroups.</summary>
    [Required, MaxLength(40)]
    public required string GroupKey { get; set; }

    [Required, MaxLength(100)]
    public required string Offense { get; set; }

    [MaxLength(80)]
    public string? OffenseCategory { get; set; }

    /// <summary>NIBRS "crime against": Person, Property, Society, Not a Crime or Non NIBRS Data.</summary>
    [MaxLength(30)]
    public string? CrimeAgainst { get; set; }

    /// <summary>
    /// False for the feed's non-crime metrics (shots-fired calls, gunshot wound
    /// victims) and for subset rows that repeat an offense already counted
    /// under its parent (domestic aggravated assault, carjacking).
    /// </summary>
    public bool IsCrime { get; set; }

    /// <summary>MPD's own offense count for the row; more than one when a case has several victims.</summary>
    public short CrimeCount { get; set; } = 1;

    [MaxLength(120)]
    public string? Neighborhood { get; set; }

    public byte? Ward { get; set; }

    public byte? Precinct { get; set; }

    public decimal? Lat { get; set; }

    public decimal? Lng { get; set; }

    /// <summary>MPD's block-range address, e.g. "0048XX 13TH AVE S".</summary>
    [MaxLength(100)]
    public string? Address { get; set; }
}
