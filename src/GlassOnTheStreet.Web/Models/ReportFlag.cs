namespace GlassOnTheStreet.Web.Models;

public class ReportFlag
{
    public int Id { get; set; }

    public int ReportId { get; set; }

    public Report? Report { get; set; }

    public string? Reason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
