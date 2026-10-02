using GlassOnTheStreet.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GlassOnTheStreet.Web.Controllers;

/// <summary>
/// One server-rendered page per neighborhood and ward. Each carries its own
/// real numbers in the HTML, so it can answer a search like "car break-ins
/// in Whittier" and be read by crawlers that don't run JavaScript.
/// </summary>
public class AreasController(ITrendsService trendsService, ICrimeStatsService crimeStats) : Controller
{
    [HttpGet("neighborhoods")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        return View(await trendsService.GetAreaIndexAsync(cancellationToken));
    }

    [HttpGet("neighborhoods/{slug}")]
    public async Task<IActionResult> Neighborhood(string slug, CancellationToken cancellationToken)
    {
        var page = await trendsService.GetNeighborhoodAsync(slug, cancellationToken);
        if (page is null)
        {
            return NotFound();
        }

        // One URL per neighborhood: send "/neighborhoods/Whittier" to the canonical slug.
        if (slug != page.Slug)
        {
            return RedirectPermanent(page.Url);
        }

        ViewData["Crime"] = await crimeStats.GetNeighborhoodSummaryAsync(slug, cancellationToken);
        return View("Detail", page);
    }

    [HttpGet("wards/{ward:int}")]
    public async Task<IActionResult> Ward(int ward, CancellationToken cancellationToken)
    {
        var page = await trendsService.GetWardAsync(ward, cancellationToken);
        if (page is null)
        {
            return NotFound();
        }

        ViewData["Crime"] = await crimeStats.GetWardSummaryAsync(ward, cancellationToken);
        return View("Detail", page);
    }
}
