using GlassOnTheStreet.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GlassOnTheStreet.Web.Controllers;

public class MethodologyController(IncidentDataCache dataCache, IPopulationService population, IConfiguration configuration) : Controller
{
    [HttpGet("methodology")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var dataset = await dataCache.GetAsync(cancellationToken);
        ViewData["Through"] = dataset.Through;
        ViewData["Population"] = population;
        ViewData["ContactEmail"] = configuration["Site:ContactEmail"];
        return View();
    }
}
