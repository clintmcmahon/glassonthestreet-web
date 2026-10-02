using GlassOnTheStreet.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GlassOnTheStreet.Web.Controllers;

public class DataController(CsvExportService csv, IncidentDataCache dataCache, IPopulationService population) : Controller
{
    [HttpGet("data")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var dataset = await dataCache.GetAsync(cancellationToken);
        ViewData["Through"] = dataset.Through;
        ViewData["Population"] = population;
        return View();
    }

    [HttpGet("data/crime-monthly.csv")]
    public async Task<IActionResult> Monthly(CancellationToken cancellationToken) =>
        File(await csv.MonthlyAsync(cancellationToken), "text/csv; charset=utf-8", "minneapolis-crime-by-month.csv");

    [HttpGet("data/crime-by-neighborhood.csv")]
    public async Task<IActionResult> ByNeighborhood(CancellationToken cancellationToken) =>
        File(await csv.ByAreaAsync(neighborhoods: true, cancellationToken), "text/csv; charset=utf-8", "minneapolis-crime-by-neighborhood.csv");

    [HttpGet("data/crime-by-ward.csv")]
    public async Task<IActionResult> ByWard(CancellationToken cancellationToken) =>
        File(await csv.ByAreaAsync(neighborhoods: false, cancellationToken), "text/csv; charset=utf-8", "minneapolis-crime-by-ward.csv");

    [HttpGet("data/population-2020.csv")]
    public async Task<IActionResult> Population(CancellationToken cancellationToken) =>
        File(await csv.PopulationAsync(cancellationToken), "text/csv; charset=utf-8", "minneapolis-population-2020.csv");
}
