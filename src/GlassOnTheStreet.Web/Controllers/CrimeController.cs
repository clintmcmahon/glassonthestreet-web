using GlassOnTheStreet.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GlassOnTheStreet.Web.Controllers;

public class CrimeController(ICrimeStatsService crimeStats) : Controller
{
    [HttpGet("crime")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        // The charts render client-side from /api/crime, but the headline and
        // the offense table are server-rendered so a crawler (or a reader
        // without JavaScript) still gets the real numbers.
        var data = await crimeStats.GetCrimeAsync(new CrimeFilter(null, null, null), false, cancellationToken);
        return View(data);
    }
}
