using GlassOnTheStreet.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GlassOnTheStreet.Web.Controllers.Api;

public record NearRequest(double Lat, double Lng, double RadiusMiles, int Days);

public record GeocodeRequest(string? Q);

/// <summary>
/// POST on purpose: the location travels in the request body, which web servers
/// and proxies don't write to their access logs the way they do a query string.
/// Nothing here stores or logs the point.
/// </summary>
[ApiController]
[EnableRateLimiting("near-lookup")]
public class NearApiController(NearbyService nearby, IGeofenceService geofence, IGeocodingService geocoding) : ControllerBase
{
    [HttpPost("api/near")]
    public async Task<IActionResult> Near([FromBody] NearRequest request, CancellationToken cancellationToken)
    {
        if (!geofence.IsWithinServiceArea((decimal)request.Lat, (decimal)request.Lng))
        {
            return BadRequest(new { error = "That location is outside Minneapolis." });
        }

        var radius = NearbyService.RadiusOptions.OrderBy(r => Math.Abs(r - request.RadiusMiles)).First();
        var days = NearbyService.DayOptions.OrderBy(d => Math.Abs(d - request.Days)).First();
        var result = await nearby.GetAsync(request.Lat, request.Lng, radius, days, cancellationToken);

        return Ok(new
        {
            radiusMiles = result.RadiusMiles,
            days = result.Days,
            from = result.From.ToString("yyyy-MM-dd"),
            through = result.Through.ToString("yyyy-MM-dd"),
            total = result.Total,
            previousTotal = result.PreviousTotal,
            groups = result.Groups.Select(g => new { key = g.Key, label = g.Label, isCrime = g.IsCrime, count = g.Count, previous = g.Previous }),
            recent = result.Recent.Select(r => new { date = r.Date.ToString("yyyy-MM-dd"), label = r.Label, offense = r.Offense, block = r.Block }),
            neighborhood = result.Neighborhood,
            neighborhoodUrl = result.NeighborhoodUrl,
            ward = result.Ward
        });
    }

    [HttpPost("api/geocode/lookup")]
    public async Task<IActionResult> Geocode([FromBody] GeocodeRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Q))
        {
            return BadRequest(new { error = "An address is required." });
        }

        var result = await geocoding.GeocodeAsync(request.Q, cancellationToken);
        return result is null
            ? NotFound(new { error = "We couldn't find that address in Minneapolis." })
            : Ok(new { lat = result.Lat, lng = result.Lng, displayName = result.DisplayName });
    }
}
