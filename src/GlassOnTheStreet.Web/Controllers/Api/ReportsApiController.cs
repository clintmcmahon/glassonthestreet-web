using GlassOnTheStreet.Web.Data;
using GlassOnTheStreet.Web.Models;
using GlassOnTheStreet.Web.Models.Api;
using GlassOnTheStreet.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace GlassOnTheStreet.Web.Controllers.Api;

[ApiController]
[Route("api/reports")]
public class ReportsApiController(
    GlassOnTheStreetContext db,
    ILocationPrivacyService privacyService,
    IGeofenceService geofenceService,
    ICaptchaService captchaService,
    IGeocodingService geocodingService,
    IReportStatsService statsService,
    IWebHostEnvironment env,
    ILogger<ReportsApiController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetReports(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken)
    {
        var query = db.Reports.Where(r => r.Status == ReportStatus.Active).AsQueryable();

        if (from is not null)
        {
            query = query.Where(r => r.ReportedDate >= from);
        }

        if (to is not null)
        {
            query = query.Where(r => r.ReportedDate <= to);
        }

        var reports = await query
            .OrderByDescending(r => r.ReportedDate)
            .Select(r => new
            {
                r.Id,
                r.DisplayLat,
                r.DisplayLng,
                r.IncidentType,
                r.TimeOfDay,
                r.ItemsStolen,
                r.PoliceReported,
                r.CrossStreets,
                r.ReportedDate,
                r.SourceType,
                r.Neighborhood,
                r.Offense
            })
            .ToListAsync(cancellationToken);

        var featureCollection = new
        {
            type = "FeatureCollection",
            features = reports.Select(r => new
            {
                type = "Feature",
                geometry = new
                {
                    type = "Point",
                    coordinates = new[] { r.DisplayLng, r.DisplayLat }
                },
                properties = new
                {
                    r.Id,
                    incidentType = r.IncidentType.ToString(),
                    timeOfDay = r.TimeOfDay?.ToString(),
                    itemsStolen = r.ItemsStolen,
                    policeReported = r.PoliceReported,
                    crossStreets = r.CrossStreets,
                    reportedDate = r.ReportedDate.ToString("yyyy-MM-dd"),
                    sourceType = r.SourceType.ToString(),
                    neighborhood = r.Neighborhood,
                    offense = r.Offense
                }
            })
        };

        return Ok(featureCollection);
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken)
    {
        var stats = await statsService.GetStatsAsync(from, to, cancellationToken);
        return Ok(new { count = stats.Count, priorCount = stats.PriorCount, percentChange = stats.PercentChange });
    }

    [HttpGet("stats/breakdown")]
    public async Task<IActionResult> GetBreakdown(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken)
    {
        var breakdown = await statsService.GetBreakdownAsync(from, to, cancellationToken);
        return Ok(new
        {
            topNeighborhoods = breakdown.TopNeighborhoods.Select(n => new { name = n.Name, count = n.Count }),
            timeOfDay = breakdown.TimeOfDay.Select(t => new { bucket = t.Bucket, count = t.Count }),
            topWards = breakdown.TopWards.Select(w => new { ward = w.Ward, count = w.Count })
        });
    }

    [HttpGet("stats/reporting-gap")]
    public async Task<IActionResult> GetReportingGap(CancellationToken cancellationToken)
    {
        var gap = await statsService.GetPoliceReportingGapAsync(cancellationToken);
        return Ok(new { respondedCount = gap.RespondedCount, percentUnreported = gap.PercentUnreported });
    }

    [HttpPost]
    [RequestSizeLimit(10_000_000)]
    [EnableRateLimiting("report-submission")]
    public async Task<IActionResult> Submit([FromForm] ReportSubmission submission, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var captchaOk = await captchaService.VerifyAsync(
            submission.CaptchaToken, HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
        if (!captchaOk)
        {
            return BadRequest(new { error = "Captcha verification failed." });
        }

        if (!geofenceService.IsWithinServiceArea(submission.Lat, submission.Lng))
        {
            return BadRequest(new { error = "Location must be within the Minneapolis metro area." });
        }

        var (displayLat, displayLng) = privacyService.SnapToBlock(submission.Lat, submission.Lng);

        // Best-effort only -- reverse geocoding the already-anonymized point
        // purely to power the neighborhood stat, never blocks a submission.
        var neighborhood = await geocodingService.ReverseGeocodeNeighborhoodAsync(displayLat, displayLng, cancellationToken);

        string? photoPath = null;
        if (submission.Photo is { Length: > 0 } photo)
        {
            var uploadsDir = Path.Combine(env.WebRootPath, "uploads");
            Directory.CreateDirectory(uploadsDir);
            var fileName = $"{Guid.NewGuid():N}{Path.GetExtension(photo.FileName)}";
            var filePath = Path.Combine(uploadsDir, fileName);
            await using (var stream = System.IO.File.Create(filePath))
            {
                await photo.CopyToAsync(stream, cancellationToken);
            }
            photoPath = $"/uploads/{fileName}";
        }

        var report = new Report
        {
            ReportedDate = submission.ReportedDate,
            DisplayLat = displayLat,
            DisplayLng = displayLng,
            IncidentType = submission.IncidentType,
            TimeOfDay = submission.TimeOfDay,
            ItemsStolen = submission.ItemsStolen,
            PoliceReported = submission.PoliceReported,
            CrossStreets = submission.CrossStreets,
            PhotoPath = photoPath,
            Neighborhood = neighborhood,
            // Every resident submission is held for admin approval before
            // it's publicly visible -- see ReportStatus.Pending.
            Status = ReportStatus.Pending
        };

        db.Reports.Add(report);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("New report {ReportId} submitted for {ReportedDate}, pending review", report.Id, report.ReportedDate);

        return CreatedAtAction(nameof(GetReports), new { id = report.Id }, new
        {
            report.Id,
            showPoliceLink = submission.PoliceReported != true
        });
    }
}
