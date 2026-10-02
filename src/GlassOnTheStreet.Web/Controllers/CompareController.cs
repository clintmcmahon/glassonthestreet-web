using GlassOnTheStreet.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GlassOnTheStreet.Web.Controllers;

public record ComparePageModel(
    CompareData Compare,
    IReadOnlyList<string> SelectedKeys,
    string? Group,
    bool ByRate,
    IReadOnlyList<string> NeighborhoodOptions,
    IReadOnlyList<int> WardOptions);

public class CompareController(ICrimeStatsService crimeStats) : Controller
{
    [HttpGet("compare")]
    public async Task<IActionResult> Index(
        [FromQuery] string? areas, [FromQuery] string? group, [FromQuery] string? metric, CancellationToken cancellationToken)
    {
        var overview = await crimeStats.GetCrimeAsync(new CrimeFilter(null, null, null), false, cancellationToken);

        var keys = (areas ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        if (keys.Count == 0)
        {
            // Citywide plus the three neighborhoods with the most offenses this year.
            keys.Add("minneapolis");
            keys.AddRange(overview.Neighborhoods.Take(3).Select(n => n.Url[(n.Url.LastIndexOf('/') + 1)..]));
        }

        var groupKey = CrimeGroups.Find(group)?.Key;
        var compare = await crimeStats.GetCompareAsync(keys, groupKey, cancellationToken);

        return View(new ComparePageModel(
            compare, compare.Series.Select(s => s.Key).ToList(), groupKey,
            string.Equals(metric, "rate", StringComparison.OrdinalIgnoreCase),
            overview.NeighborhoodOptions, overview.WardOptions));
    }
}
