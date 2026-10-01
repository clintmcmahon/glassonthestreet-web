using GlassOnTheStreet.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GlassOnTheStreet.Web.Controllers.Api;

[ApiController]
[Route("api/trends")]
public class TrendsApiController(ITrendsService trendsService) : ControllerBase
{
    [HttpGet]
    [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Client)]
    public async Task<IActionResult> Get(
        [FromQuery] string? category, [FromQuery] string? neighborhood, [FromQuery] int? ward,
        CancellationToken cancellationToken)
    {
        var filter = new TrendFilter(
            TrendsService.ParseCategory(category),
            string.IsNullOrWhiteSpace(neighborhood) ? null : neighborhood.Trim(),
            ward);

        var data = await trendsService.GetTrendsAsync(filter, cancellationToken);

        return Ok(new
        {
            years = data.Years,
            currentYear = data.CurrentYear,
            through = data.Through.ToString("yyyy-MM-dd"),
            monthly = data.Monthly,
            samePeriod = data.SamePeriod,
            neighborhoods = data.Neighborhoods.Select(a => new { name = a.Name, counts = a.Counts, changeVsPrior = a.ChangeVsPrior, url = a.Url }),
            wards = data.Wards.Select(a => new { name = a.Name, counts = a.Counts, changeVsPrior = a.ChangeVsPrior, url = a.Url }),
            options = new { neighborhoods = data.NeighborhoodOptions, wards = data.WardOptions }
        });
    }
}
