using GlassOnTheStreet.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GlassOnTheStreet.Web.Controllers.Api;

[ApiController]
[Route("api/geocode")]
[EnableRateLimiting("geocode-search")]
public class GeocodeApiController(IGeocodingService geocodingService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] string q, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return BadRequest(new { error = "q is required." });
        }

        var result = await geocodingService.GeocodeAsync(q, cancellationToken);
        if (result is null)
        {
            return NotFound();
        }

        return Ok(new { lat = result.Lat, lng = result.Lng, displayName = result.DisplayName });
    }
}
