using GlassOnTheStreet.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GlassOnTheStreet.Web.Controllers;

public record EmbedYearBarsModel(
    string Title, int[] Years, int[] Counts, DateOnly Through, string UnitLabel, string SourcePath);

/// <summary>
/// A chart someone else can put in an iframe. Server-rendered SVG, no JavaScript, with the
/// attribution and a link back built in, so a reposted chart always carries its source.
/// </summary>
public class EmbedController(ICrimeStatsService crimeStats, ITrendsService trendsService) : Controller
{
    [HttpGet("embed/year-bars")]
    public async Task<IActionResult> YearBars(
        [FromQuery] string? scope, [FromQuery] string? category, [FromQuery] string? neighborhood, [FromQuery] int? ward,
        CancellationToken cancellationToken)
    {
        var hood = string.IsNullOrWhiteSpace(neighborhood) ? null : neighborhood.Trim();
        var parts = new List<string>();
        string title, unit, source;
        int[] years, counts;
        DateOnly through;

        if (string.Equals(scope, "car", StringComparison.OrdinalIgnoreCase))
        {
            var type = TrendsService.ParseCategory(category);
            var data = await trendsService.GetTrendsAsync(new TrendFilter(type, hood, ward), cancellationToken);
            years = data.Years;
            counts = data.SamePeriod;
            through = data.Through;
            title = type is null ? "Car break-in, auto theft and vandalism reports" : TrendsService.CategoryLabel(type.Value) + " reports";
            unit = "reports";
            source = "/trends";
        }
        else
        {
            var group = CrimeGroups.Find(category);
            var data = await crimeStats.GetCrimeAsync(new CrimeFilter(group?.Key, hood, ward), false, cancellationToken);
            years = data.Years;
            counts = data.SamePeriod;
            through = data.Through;
            title = (group?.Label ?? "All crimes");
            unit = "offenses";
            source = "/crime";
        }

        if (hood is not null)
        {
            parts.Add(hood);
        }
        else if (ward is not null)
        {
            parts.Add($"Ward {ward}");
        }
        else
        {
            parts.Add("Minneapolis");
        }

        // Keep the query on the link back, so the viewer lands on the same slice.
        var query = string.Join("&", new[]
        {
            category is null ? null : $"category={Uri.EscapeDataString(category)}",
            hood is null ? null : $"neighborhood={Uri.EscapeDataString(hood)}",
            ward is null ? null : $"ward={ward}"
        }.Where(q => q is not null));

        return View(new EmbedYearBarsModel(
            $"{title}, {string.Join(", ", parts)}", years, counts, through, unit, source + (query.Length > 0 ? "?" + query : "")));
    }
}
