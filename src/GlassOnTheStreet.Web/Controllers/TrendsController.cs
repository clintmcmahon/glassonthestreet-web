using GlassOnTheStreet.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GlassOnTheStreet.Web.Controllers;

public class TrendsController(ITrendsService trendsService) : Controller
{
    [HttpGet("trends")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        // The charts render client-side from /api/trends, but the headline
        // and the same-period table are server-rendered so a crawler (or a
        // reader without JavaScript) still gets the real numbers.
        var data = await trendsService.GetTrendsAsync(new TrendFilter(null, null, null), cancellationToken);
        return View(data);
    }
}
