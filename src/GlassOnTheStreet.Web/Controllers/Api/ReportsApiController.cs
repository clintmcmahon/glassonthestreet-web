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
                r.ReportedDate
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
                    reportedDate = r.ReportedDate.ToString("yyyy-MM-dd")
                }
            })
        };

        return Ok(featureCollection);
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken)
    {
        var rangeTo = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var rangeFrom = from ?? rangeTo.AddDays(-30);
        var rangeDays = rangeTo.DayNumber - rangeFrom.DayNumber + 1;

        var priorTo = rangeFrom.AddDays(-1);
        var priorFrom = priorTo.AddDays(-(rangeDays - 1));

        var activeReports = db.Reports.Where(r => r.Status == ReportStatus.Active);

        var currentCount = await activeReports
            .CountAsync(r => r.ReportedDate >= rangeFrom && r.ReportedDate <= rangeTo, cancellationToken);
        var priorCount = await activeReports
            .CountAsync(r => r.ReportedDate >= priorFrom && r.ReportedDate <= priorTo, cancellationToken);

        double? percentChange = priorCount == 0
            ? null
            : Math.Round((currentCount - priorCount) / (double)priorCount * 100, 1);

        return Ok(new { count = currentCount, priorCount, percentChange });
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
            PreciseLat = submission.Lat,
            PreciseLng = submission.Lng,
            DisplayLat = displayLat,
            DisplayLng = displayLng,
            IncidentType = submission.IncidentType,
            TimeOfDay = submission.TimeOfDay,
            ItemsStolen = submission.ItemsStolen,
            PoliceReported = submission.PoliceReported,
            CrossStreets = submission.CrossStreets,
            PhotoPath = photoPath
        };

        db.Reports.Add(report);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("New report {ReportId} submitted for {ReportedDate}", report.Id, report.ReportedDate);

        return CreatedAtAction(nameof(GetReports), new { id = report.Id }, new
        {
            report.Id,
            showPoliceLink = submission.PoliceReported != true
        });
    }
}
