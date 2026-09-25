using System.ComponentModel.DataAnnotations;

namespace GlassOnTheStreet.Web.Models.Api;

public class ReportSubmission
{
    [Required]
    public DateOnly ReportedDate { get; set; }

    [Required]
    public decimal Lat { get; set; }

    [Required]
    public decimal Lng { get; set; }

    [Required]
    public IncidentType IncidentType { get; set; }

    public TimeOfDay? TimeOfDay { get; set; }

    public bool? ItemsStolen { get; set; }

    public bool? PoliceReported { get; set; }

    [MaxLength(200)]
    public string? CrossStreets { get; set; }

    public IFormFile? Photo { get; set; }

    public string? CaptchaToken { get; set; }
}
