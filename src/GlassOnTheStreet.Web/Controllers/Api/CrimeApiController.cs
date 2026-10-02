using GlassOnTheStreet.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GlassOnTheStreet.Web.Controllers.Api;

[ApiController]
[Route("api/crime")]
public class CrimeApiController(ICrimeStatsService crimeStats) : ControllerBase
{
    [HttpGet]
    [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Client)]
    public async Task<IActionResult> Get(
        [FromQuery] string? category, [FromQuery] string? neighborhood, [FromQuery] int? ward,
        [FromQuery] string? sort, CancellationToken cancellationToken)
    {
        var filter = new CrimeFilter(
            CrimeGroups.Find(category)?.Key,
            string.IsNullOrWhiteSpace(neighborhood) ? null : neighborhood.Trim(),
            ward);

        var data = await crimeStats.GetCrimeAsync(filter, string.Equals(sort, "rate", StringComparison.OrdinalIgnoreCase), cancellationToken);

        object Area(AreaTrend a) => new
        {
            name = a.Name, counts = a.Counts, changeVsPrior = a.ChangeVsPrior, url = a.Url,
            ratePerThousand = a.RatePerThousand, population = a.Population
        };

        object Row(GroupRow g) => new
        {
            key = g.Key, label = g.Label, definition = g.Definition, isCrime = g.IsCrime, counts = g.Counts,
            changeVsPrior = g.ChangeVsPrior, changeVsBase = g.ChangeVsBase, ratePerThousand = g.RatePerThousand
        };

        return Ok(new
        {
            years = data.Years,
            currentYear = data.CurrentYear,
            through = data.Through.ToString("yyyy-MM-dd"),
            monthly = data.Monthly,
            samePeriod = data.SamePeriod,
            neighborhoods = data.Neighborhoods.Select(Area),
            wards = data.Wards.Select(Area),
            groups = data.Groups.Select(Row),
            metrics = data.Metrics.Select(Row),
            hourWeekday = data.HourWeekday,
            hourWeekdayTotal = data.HourWeekdayTotal,
            population = data.Population,
            ratePerThousand = data.RatePerThousand,
            scope = data.ScopeLabel,
            options = new { neighborhoods = data.NeighborhoodOptions, wards = data.WardOptions }
        });
    }
}
